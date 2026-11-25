namespace TriageDesk.Api.Modules.TriageAndActivity;

public sealed class IssueTriage
{
    public Guid WorkspaceId { get; set; }
    public Guid IssueId { get; set; }
    public string Status { get; set; } = "needsReview";
    public string? Priority { get; set; }
    public string? Severity { get; set; }
    public Guid? AssigneeId { get; set; }
    public string? NextStep { get; set; }
    public string? Question { get; set; }
    public string? DeferralReason { get; set; }
    public string? SourceReviewFlag { get; set; }
    public DateTimeOffset? EnteredReviewAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int DecisionVersion { get; set; } = 1;
}
