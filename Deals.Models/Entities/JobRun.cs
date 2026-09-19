using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Deals.Models.Entities;

/// <summary>
/// One execution of a periodic HostedService. Deliberately does NOT inherit <c>BaseModel</c>: the table
/// has no <c>created_at</c>/<c>updated_at</c>/<c>created_by</c>/<c>updated_by</c> columns — a run is an
/// event, not an editable row, so <see cref="StartedAt"/>/<see cref="FinishedAt"/> are its whole audit.
/// Exists so a job can ask "did I already run in the last N hours?" instead of replaying a full pass on
/// every process start, and so the operator can see what ran and what it produced.
/// </summary>
[Table("job_runs")]
public class JobRun
{
    [Key]
    [Column("job_run_id")]
    public long JobRunId { get; set; }

    [Column("job")]
    [Required]
    [StringLength(64)]
    public string Job { get; set; } = string.Empty;

    [Column("trigger")]
    [Required]
    [StringLength(16)]
    public string Trigger { get; set; } = string.Empty;

    [Column("status")]
    [Required]
    [StringLength(16)]
    public string Status { get; set; } = string.Empty;

    [Column("started_at", TypeName = "timestamp with time zone")]
    public DateTime StartedAt { get; set; }

    [Column("finished_at", TypeName = "timestamp with time zone")]
    public DateTime? FinishedAt { get; set; }

    /// <summary>JSONB held as text: counters and statuses of the cycle. Never secrets.</summary>
    [Column("details")]
    public string? Details { get; set; }
}

/// <summary>Persisted run outcomes. Mirrors <c>WishlistStates</c>: plain string constants, no enum.</summary>
public static class JobRunStatuses
{
    /// <summary>Written before the work starts. A row still in this state means the process died mid-cycle.</summary>
    public const string Running = "running";

    public const string Ok = "ok";
    public const string Failed = "failed";
}
