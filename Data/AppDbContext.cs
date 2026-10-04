using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MusicRoyaltyApi.Models;
using Npgsql;

namespace MusicRoyaltyApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Contribution> Contributions => Set<Contribution>();
    public DbSet<RevenueEvent> RevenueEvents => Set<RevenueEvent>();
    public DbSet<EventTrack> EventTracks => Set<EventTrack>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<EarningsPerUser> EarningsPerUsers => Set<EarningsPerUser>();
    public DbSet<RevenueBySourceMonth> RevenueBySourceMonths => Set<RevenueBySourceMonth>();
    public DbSet<TopTrack> TopTracks => Set<TopTrack>();
    public DbSet<ContributionIssue> ContributionIssues => Set<ContributionIssue>();
    public DbSet<PendingPayout> PendingPayouts => Set<PendingPayout>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AuditLog>().Property(a => a.RowData).HasColumnType("jsonb");

        b.Entity<EarningsPerUser>().HasNoKey().ToView("v_earnings_per_user");
        b.Entity<RevenueBySourceMonth>().HasNoKey().ToView("v_revenue_by_source_month");
        b.Entity<TopTrack>().HasNoKey().ToView("v_top_tracks");
        b.Entity<ContributionIssue>().HasNoKey().ToView("v_contribution_issues");
        b.Entity<PendingPayout>().HasNoKey().ToView("v_pending_payouts");
    }
}

// Turns PostgreSQL errors (duplicates, trigger rejections, FK violations) into clear API responses.
public static class DbErrors
{
    public static IActionResult? ToResult(Exception ex)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        if (pg == null) return null;

        return pg.SqlState switch
        {
            "23505" => new ConflictObjectResult("That value already exists (duplicate email, Spotify ID or contribution)."),
            "23503" => new ConflictObjectResult("This record is linked to other data and cannot be changed or deleted."),
            "23514" => new BadRequestObjectResult("A value failed a database check: " + pg.MessageText),
            "P0001" => new BadRequestObjectResult(pg.MessageText),
            _ => null
        };
    }
}
