using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicRoyaltyApi.Data;
using MusicRoyaltyApi.Models;
using Npgsql;

namespace MusicRoyaltyApi.Controllers;

// ---------- Revenue: create events, allocate, pay ----------

[ApiController]
[Route("api/[controller]")]
public class RevenueController : ControllerBase
{
    private readonly AppDbContext _context;

    public RevenueController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("sources")]
    public async Task<IActionResult> GetSources() =>
        Ok(await _context.Sources.OrderBy(s => s.SourceName).ToListAsync());

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents()
    {
        var events = await (from e in _context.RevenueEvents
                            join s in _context.Sources on e.SourceId equals s.SourceId
                            orderby e.EventDate descending
                            select new
                            {
                                e.RevenueEventId,
                                Source = s.SourceName,
                                e.GrossAmount,
                                e.EventDate,
                                Allocated = _context.EventTracks.Any(t => t.RevenueEventId == e.RevenueEventId)
                            }).ToListAsync();
        return Ok(events);
    }

    [HttpPost("events")]
    public async Task<IActionResult> CreateEvent(CreateRevenueEventRequest request)
    {
        if (request.GrossAmount <= 0)
            return BadRequest("Gross amount must be greater than 0.");

        if (!await _context.Sources.AnyAsync(s => s.SourceId == request.SourceId))
            return BadRequest("Source does not exist.");

        var revenueEvent = new RevenueEvent
        {
            SourceId = request.SourceId,
            GrossAmount = request.GrossAmount,
            EventDate = request.EventDate ?? DateOnly.FromDateTime(DateTime.UtcNow)
        };

        try
        {
            _context.RevenueEvents.Add(revenueEvent);
            await _context.SaveChangesAsync();
            return Ok(revenueEvent);
        }
        catch (DbUpdateException ex)
        {
            return DbErrors.ToResult(ex) ?? StatusCode(500, "Failed to create revenue event.");
        }
    }

    // Splits the gross amount across tracks by plays. The database trigger then creates the payouts.
    [HttpPost("events/{id}/allocate")]
    public async Task<IActionResult> Allocate(int id)
    {
        try
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT allocate_revenue({id})");
            var count = await _context.EventTracks.CountAsync(e => e.RevenueEventId == id);
            return Ok(new { RevenueEventId = id, TracksAllocated = count });
        }
        catch (PostgresException ex)
        {
            return BadRequest(ex.MessageText);
        }
    }

    // Marks all pending payouts for the given month as paid.
    [HttpPost("pay/{year}/{month}")]
    public async Task<IActionResult> PayPending(int year, int month)
    {
        if (month < 1 || month > 12)
            return BadRequest("Month must be between 1 and 12.");

        var pendingCount = await (from p in _context.Payouts
                                  join et in _context.EventTracks on p.EventTrackId equals et.EventTrackId
                                  join re in _context.RevenueEvents on et.RevenueEventId equals re.RevenueEventId
                                  where p.PayoutStatus == "Pending"
                                        && re.EventDate.Year == year
                                        && re.EventDate.Month == month
                                  select p.PayoutId).CountAsync();

        try
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT pay_pending({year}, {month})");
            return Ok(new { Year = year, Month = month, PayoutsMarkedPaid = pendingCount });
        }
        catch (PostgresException ex)
        {
            return BadRequest(ex.MessageText);
        }
    }
}

public class CreateRevenueEventRequest
{
    public int SourceId { get; set; }
    public decimal GrossAmount { get; set; }
    public DateOnly? EventDate { get; set; }
}

// ---------- Reports (read from the database views) ----------

[ApiController]
[Route("api/[controller]")]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _context;

    public ReportsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet("earnings")]
    public async Task<IActionResult> Earnings() =>
        Ok(await _context.EarningsPerUsers.OrderByDescending(e => e.TotalEarned).ToListAsync());

    [HttpGet("revenue-by-source")]
    public async Task<IActionResult> RevenueBySource() =>
        Ok(await _context.RevenueBySourceMonths.OrderBy(r => r.Month).ThenBy(r => r.SourceName).ToListAsync());

    [HttpGet("top-tracks")]
    public async Task<IActionResult> TopTracks() =>
        Ok(await _context.TopTracks.OrderByDescending(t => t.TotalAllocated).ToListAsync());

    [HttpGet("contribution-issues")]
    public async Task<IActionResult> ContributionIssues() =>
        Ok(await _context.ContributionIssues.ToListAsync());

    [HttpGet("pending-payouts")]
    public async Task<IActionResult> PendingPayouts() =>
        Ok(await _context.PendingPayouts.OrderBy(p => p.FullName).ToListAsync());

    [HttpGet("audit-log")]
    public async Task<IActionResult> AuditLog() =>
        Ok(await _context.AuditLogs.OrderByDescending(a => a.LogId).Take(100).ToListAsync());
}

// ---------- Table manager: list, create and drop tables ----------

[ApiController]
[Route("api/[controller]")]
public class TablesController : ControllerBase
{
    private static readonly Regex NameRegex = new("^[a-z_][a-z0-9_]{0,62}$");

    private static readonly HashSet<string> CoreTables = new()
    {
        "users", "bank_accounts", "roles", "sources", "tracks", "contributions",
        "revenue_events", "event_tracks", "payouts", "audit_log"
    };

    private static readonly Dictionary<string, string> PkMap = new()
    {
        ["users"] = "user_id", ["bank_accounts"] = "bank_account_id", ["roles"] = "role_id",
        ["sources"] = "source_id", ["tracks"] = "track_id", ["contributions"] = "contribution_id",
        ["revenue_events"] = "revenue_event_id", ["event_tracks"] = "event_track_id", ["payouts"] = "payout_id"
    };

    private static readonly Dictionary<string, string> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = "TEXT",
        ["integer"] = "INTEGER",
        ["decimal"] = "NUMERIC(12,2)",
        ["date"] = "DATE",
        ["boolean"] = "BOOLEAN",
        ["timestamp"] = "TIMESTAMP"
    };

    private readonly AppDbContext _context;

    public TablesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetTables()
    {
        var tables = await ListTablesAsync();
        return Ok(tables.Select(t => new { Name = t, IsCore = CoreTables.Contains(t) }));
    }

    [HttpGet("types")]
    public IActionResult GetTypes() => Ok(TypeMap.Keys);

    [HttpPost]
    public async Task<IActionResult> CreateTable(CreateTableRequest request)
    {
        var name = (request.TableName ?? string.Empty).Trim().ToLowerInvariant();

        if (!NameRegex.IsMatch(name))
            return BadRequest("Table name must use lowercase letters, digits and underscores, and cannot start with a digit.");

        if (CoreTables.Contains(name) || (await ListTablesAsync()).Contains(name))
            return BadRequest($"A table named '{name}' already exists.");

        if (request.Columns == null || request.Columns.Count == 0)
            return BadRequest("Add at least one column.");

        var columnSql = new List<string> { "id SERIAL PRIMARY KEY" };
        var seen = new HashSet<string> { "id" };

        foreach (var col in request.Columns)
        {
            var colName = (col.Name ?? string.Empty).Trim().ToLowerInvariant();

            if (!NameRegex.IsMatch(colName))
                return BadRequest($"Invalid column name '{col.Name}'.");

            if (!seen.Add(colName))
                return BadRequest($"Duplicate column name '{colName}' (an 'id' column is added automatically).");

            if (!TypeMap.TryGetValue(col.Type ?? string.Empty, out var sqlType))
                return BadRequest($"Unsupported type '{col.Type}'. Allowed: {string.Join(", ", TypeMap.Keys)}.");

            var fk = "";
            if (!string.IsNullOrWhiteSpace(col.ReferencesTable))
            {
                var rt = col.ReferencesTable.Trim().ToLowerInvariant();
                if (!NameRegex.IsMatch(rt))
                    return BadRequest($"Invalid table name '{col.ReferencesTable}' in link.");
                if (!PkMap.TryGetValue(rt, out var pk))
                {
                    if (!(await ListTablesAsync()).Contains(rt))
                        return BadRequest($"Cannot link to '{rt}'.");
                    pk = "id";
                }
                fk = $" REFERENCES \"{rt}\"({pk})";
                sqlType = "INTEGER";
            }

            columnSql.Add($"\"{colName}\" {sqlType}{fk}");
        }

        var sql = $"CREATE TABLE \"{name}\" ({string.Join(", ", columnSql)})";

        try
        {
            await _context.Database.ExecuteSqlRawAsync(sql);
            return Ok(new { Created = name });
        }
        catch (PostgresException ex)
        {
            return BadRequest(ex.MessageText);
        }
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> DropTable(string name)
    {
        name = name.Trim().ToLowerInvariant();

        if (!NameRegex.IsMatch(name))
            return BadRequest("Invalid table name.");

        if (CoreTables.Contains(name))
            return BadRequest($"'{name}' is a core table and cannot be dropped.");

        if (!(await ListTablesAsync()).Contains(name))
            return NotFound($"Table '{name}' does not exist.");

        try
        {
            await _context.Database.ExecuteSqlRawAsync($"DROP TABLE \"{name}\"");
            return Ok(new { Dropped = name });
        }
        catch (PostgresException ex)
        {
            return BadRequest(ex.MessageText);
        }
    }

    private async Task<List<string>> ListTablesAsync()
    {
        var tables = new List<string>();
        var conn = _context.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync();

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT table_name FROM information_schema.tables " +
                              "WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }

        return tables;
    }
}

public class CreateTableRequest
{
    public string TableName { get; set; } = string.Empty;
    public List<NewColumn> Columns { get; set; } = new();
}

public class NewColumn
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? ReferencesTable { get; set; }
}
