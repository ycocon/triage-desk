using System.Text.Json;

namespace TriageDesk.Api.Modules.TriageAndActivity;

public sealed class IssueActivity
{
    public Guid WorkspaceId { get; set; }
    public Guid Id { get; set; }
    public Guid IssueId { get; set; }
    public Guid? ActorId { get; set; }
    public Guid OperationId { get; set; }
    public int Ordinal { get; set; }
    public string? CommandDigest { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public JsonElement Changes { get; set; }
    public string? Reason { get; set; }
}

internal sealed record ActivityChange(string Field, object? Before, object? After);
internal sealed record ActivityView(Guid ActivityId, Guid IssueId, Guid? ActorId, string? ActorName,
    string ActorKind, string Kind, DateTimeOffset OccurredAt, JsonElement Changes, string? Reason);
