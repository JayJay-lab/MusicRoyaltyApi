using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MusicRoyaltyApi.Models;

[Table("users")]
public class User
{
    [Key]
    [Column("user_id")]
    public int UserId { get; set; }

    [Column("full_name")]
    public string FullName { get; set; } = string.Empty;

    [Column("artist_name")]
    public string? ArtistName { get; set; }

    [Column("spotify_id")]
    public string? SpotifyId { get; set; }

    [Column("email")]
    public string Email { get; set; } = string.Empty;
}

[Table("bank_accounts")]
public class BankAccount
{
    [Key]
    [Column("bank_account_id")]
    public int BankAccountId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("bank_name")]
    public string BankName { get; set; } = string.Empty;

    [Column("account_number")]
    public string AccountNumber { get; set; } = string.Empty;

    [Column("branch_code")]
    public string BranchCode { get; set; } = string.Empty;
}

[Table("roles")]
public class Role
{
    [Key]
    [Column("role_id")]
    public int RoleId { get; set; }

    [Column("role_name")]
    public string RoleName { get; set; } = string.Empty;
}

[Table("sources")]
public class Source
{
    [Key]
    [Column("source_id")]
    public int SourceId { get; set; }

    [Column("source_name")]
    public string SourceName { get; set; } = string.Empty;
}

[Table("tracks")]
public class Track
{
    [Key]
    [Column("track_id")]
    public int TrackId { get; set; }

    [Column("uploaded_by")]
    public int UploadedByUserId { get; set; }

    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("date_uploaded")]
    public DateOnly DateUploaded { get; set; }

    [Column("plays")]
    public int Plays { get; set; }

    [Column("duration")]
    public int Duration { get; set; }
}

[Table("contributions")]
public class Contribution
{
    [Key]
    [Column("contribution_id")]
    public int ContributionId { get; set; }

    [Column("track_id")]
    public int TrackId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("role_id")]
    public int RoleId { get; set; }

    [Column("percentage_cut")]
    public decimal PercentageCut { get; set; }
}

[Table("revenue_events")]
public class RevenueEvent
{
    [Key]
    [Column("revenue_event_id")]
    public int RevenueEventId { get; set; }

    [Column("source_id")]
    public int SourceId { get; set; }

    [Column("gross_amount")]
    public decimal GrossAmount { get; set; }

    [Column("event_date")]
    public DateOnly EventDate { get; set; }
}

[Table("event_tracks")]
public class EventTrack
{
    [Key]
    [Column("event_track_id")]
    public int EventTrackId { get; set; }

    [Column("revenue_event_id")]
    public int RevenueEventId { get; set; }

    [Column("track_id")]
    public int TrackId { get; set; }

    [Column("allocated_amount")]
    public decimal AllocatedAmount { get; set; }
}

// Payouts are created by the database trigger, the API only reads and updates them.
[Table("payouts")]
public class Payout
{
    [Key]
    [Column("payout_id")]
    public int PayoutId { get; set; }

    [Column("event_track_id")]
    public int EventTrackId { get; set; }

    [Column("contribution_id")]
    public int ContributionId { get; set; }

    [Column("amount_paid")]
    public decimal AmountPaid { get; set; }

    [Column("payout_status")]
    public string PayoutStatus { get; set; } = "Pending";

    [Column("calculation_date")]
    public DateTime? CalculationDate { get; set; }

    [Column("payout_date")]
    public DateOnly? PayoutDate { get; set; }
}

[Table("audit_log")]
public class AuditLog
{
    [Key]
    [Column("log_id")]
    public int LogId { get; set; }

    [Column("table_name")]
    public string TableName { get; set; } = string.Empty;

    [Column("action")]
    public string Action { get; set; } = string.Empty;

    [Column("changed_by")]
    public string ChangedBy { get; set; } = string.Empty;

    [Column("changed_at")]
    public DateTime ChangedAt { get; set; }

    [Column("row_data")]
    public string? RowData { get; set; }
}

// ---------- Report views (read only, no key) ----------

public class EarningsPerUser
{
    [Column("user_id")] public int UserId { get; set; }
    [Column("full_name")] public string FullName { get; set; } = string.Empty;
    [Column("total_earned")] public decimal TotalEarned { get; set; }
    [Column("pending")] public decimal Pending { get; set; }
    [Column("paid")] public decimal Paid { get; set; }
}

public class RevenueBySourceMonth
{
    [Column("source_name")] public string SourceName { get; set; } = string.Empty;
    [Column("month")] public DateOnly Month { get; set; }
    [Column("gross_total")] public decimal GrossTotal { get; set; }
}

public class TopTrack
{
    [Column("track_id")] public int TrackId { get; set; }
    [Column("title")] public string Title { get; set; } = string.Empty;
    [Column("plays")] public int Plays { get; set; }
    [Column("total_allocated")] public decimal TotalAllocated { get; set; }
}

public class ContributionIssue
{
    [Column("track_id")] public int TrackId { get; set; }
    [Column("title")] public string Title { get; set; } = string.Empty;
    [Column("total_percentage")] public decimal TotalPercentage { get; set; }
}

public class PendingPayout
{
    [Column("payout_id")] public int PayoutId { get; set; }
    [Column("full_name")] public string FullName { get; set; } = string.Empty;
    [Column("title")] public string Title { get; set; } = string.Empty;
    [Column("amount_paid")] public decimal AmountPaid { get; set; }
    [Column("bank_name")] public string? BankName { get; set; }
    [Column("account_number")] public string? AccountNumber { get; set; }
}
