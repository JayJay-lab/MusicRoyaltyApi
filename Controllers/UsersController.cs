using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicRoyaltyApi.Data;
using MusicRoyaltyApi.Models;

namespace MusicRoyaltyApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;

    public UsersController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _context.Users.OrderBy(u => u.FullName).ToListAsync();
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser(CreateUserRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Full name and email are required.");

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var user = new User
            {
                FullName = request.FullName.Trim(),
                ArtistName = request.ArtistName,
                SpotifyId = string.IsNullOrWhiteSpace(request.SpotifyId) ? null : request.SpotifyId,
                Email = request.Email.Trim()
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            if (request.BankAccount != null)
            {
                _context.BankAccounts.Add(new BankAccount
                {
                    UserId = user.UserId,
                    BankName = request.BankAccount.BankName,
                    AccountNumber = request.BankAccount.AccountNumber,
                    BranchCode = request.BankAccount.BranchCode
                });
                await _context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            return Ok(user);
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync();
            return DbErrors.ToResult(ex) ?? StatusCode(500, "Failed to create user.");
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateUser(int id, UpdateUserRequest request)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
            return NotFound($"User with ID {id} not found.");

        if (string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Full name and email are required.");

        user.FullName = request.FullName.Trim();
        user.ArtistName = request.ArtistName;
        user.SpotifyId = string.IsNullOrWhiteSpace(request.SpotifyId) ? null : request.SpotifyId;
        user.Email = request.Email.Trim();

        try
        {
            await _context.SaveChangesAsync();
            return Ok(user);
        }
        catch (DbUpdateException ex)
        {
            return DbErrors.ToResult(ex) ?? StatusCode(500, "Failed to update user.");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
            return NotFound($"User with ID {id} not found.");

        try
        {
            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            return NoContent();
        }
        catch (DbUpdateException ex)
        {
            return DbErrors.ToResult(ex) ?? StatusCode(500, "Failed to delete user.");
        }
    }

    [HttpGet("{id}/balance")]
    public async Task<IActionResult> GetUserBalance(int id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
            return NotFound($"User with ID {id} not found.");

        var bankAccounts = await _context.BankAccounts
            .Where(b => b.UserId == id)
            .ToListAsync();

        var payouts = await (from p in _context.Payouts
                             join c in _context.Contributions on p.ContributionId equals c.ContributionId
                             join r in _context.Roles on c.RoleId equals r.RoleId
                             join t in _context.Tracks on c.TrackId equals t.TrackId
                             where c.UserId == id
                             select new
                             {
                                 p.PayoutId,
                                 p.AmountPaid,
                                 p.PayoutStatus,
                                 p.CalculationDate,
                                 p.PayoutDate,
                                 Role = r.RoleName,
                                 t.TrackId,
                                 TrackTitle = t.Title
                             }).ToListAsync();

        var pending = payouts.Where(p => p.PayoutStatus == "Pending").Sum(p => p.AmountPaid);
        var paid = payouts.Where(p => p.PayoutStatus == "Paid").Sum(p => p.AmountPaid);

        return Ok(new
        {
            user.UserId,
            user.FullName,
            user.ArtistName,
            BankAccounts = bankAccounts,
            TotalBalanceDue = pending,
            TotalPaid = paid,
            PayoutBreakdown = payouts
        });
    }

    // Upload a track and its contributions together, under a specific user
    [HttpPost("{id}/tracks")]
    public async Task<IActionResult> UploadTrackWithContributions(int id, TrackUploadRequest request)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
            return NotFound($"User with ID {id} not found.");

        if (string.IsNullOrWhiteSpace(request.Title) || request.Duration <= 0)
            return BadRequest("A title and a duration greater than 0 are required.");

        if (request.Contributions.Count == 0)
            return BadRequest("At least one contribution is required.");

        if (request.Contributions.Any(c => c.PercentageCut <= 0))
            return BadRequest("Every percentage_cut must be greater than 0.");

        if (request.Contributions.Sum(c => c.PercentageCut) > 100)
            return BadRequest("Total percentage_cut across contributions cannot exceed 100.");

        if (request.Contributions.GroupBy(c => new { c.UserId, c.RoleId }).Any(g => g.Count() > 1))
            return BadRequest("The same user cannot have the same role twice on one track.");

        var userIds = request.Contributions.Select(c => c.UserId).Distinct().ToList();
        if (await _context.Users.CountAsync(u => userIds.Contains(u.UserId)) != userIds.Count)
            return BadRequest("One or more contributors do not exist.");

        var roleIds = request.Contributions.Select(c => c.RoleId).Distinct().ToList();
        if (await _context.Roles.CountAsync(r => roleIds.Contains(r.RoleId)) != roleIds.Count)
            return BadRequest("One or more roles do not exist.");

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var track = new Track
            {
                UploadedByUserId = id,
                Title = request.Title.Trim(),
                Duration = request.Duration,
                DateUploaded = DateOnly.FromDateTime(DateTime.UtcNow),
                Plays = 0
            };

            _context.Tracks.Add(track);
            await _context.SaveChangesAsync();

            foreach (var c in request.Contributions)
            {
                _context.Contributions.Add(new Contribution
                {
                    TrackId = track.TrackId,
                    UserId = c.UserId,
                    RoleId = c.RoleId,
                    PercentageCut = c.PercentageCut
                });
            }

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new
            {
                track.TrackId,
                track.Title,
                track.DateUploaded,
                Contributions = request.Contributions
            });
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync();
            return DbErrors.ToResult(ex) ?? StatusCode(500, "Failed to upload track and contributions.");
        }
    }

    // Delete a track and its contributors. Only the uploader can do this, and only
    // while the track has not earned any revenue.
    [HttpDelete("{id}/tracks/{trackId}")]
    public async Task<IActionResult> DeleteTrack(int id, int trackId)
    {
        var track = await _context.Tracks.FindAsync(trackId);
        if (track == null)
            return NotFound("Track not found.");

        if (track.UploadedByUserId != id)
            return BadRequest("Only the person who uploaded a track can delete it.");

        if (await _context.EventTracks.AnyAsync(e => e.TrackId == trackId))
            return BadRequest("This track has already earned revenue, so it cannot be deleted.");

        var contributions = await _context.Contributions.Where(c => c.TrackId == trackId).ToListAsync();
        _context.Contributions.RemoveRange(contributions);
        _context.Tracks.Remove(track);

        try
        {
            await _context.SaveChangesAsync();
            return Ok(new { deleted = 1 });
        }
        catch (DbUpdateException ex)
        {
            return DbErrors.ToResult(ex) ?? StatusCode(500, "Failed to delete track.");
        }
    }
}

public class BankAccountRequest
{
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
}

public class CreateUserRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? ArtistName { get; set; }
    public string? SpotifyId { get; set; }
    public string Email { get; set; } = string.Empty;
    public BankAccountRequest? BankAccount { get; set; }
}

public class UpdateUserRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? ArtistName { get; set; }
    public string? SpotifyId { get; set; }
    public string Email { get; set; } = string.Empty;
}

public class TrackUploadRequest
{
    public string Title { get; set; } = string.Empty;
    public int Duration { get; set; }
    public List<ContributionUploadRequest> Contributions { get; set; } = new();
}

public class ContributionUploadRequest
{
    public int UserId { get; set; }
    public int RoleId { get; set; }
    public decimal PercentageCut { get; set; }
}
