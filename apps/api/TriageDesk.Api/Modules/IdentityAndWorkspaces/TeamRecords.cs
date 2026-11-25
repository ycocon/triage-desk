namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

public sealed class WorkspaceInvitation
{
    public Guid WorkspaceId { get; set; }
    public Guid Id { get; set; }
    public Guid InviterUserId { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string Role { get; set; }
    public required string TokenDigest { get; set; }
    public string State { get; set; } = "pending";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class TeamAuditEntry
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? SubjectUserId { get; set; }
    public Guid? InvitationId { get; set; }
    public required string Action { get; set; }
    public string? PreviousRole { get; set; }
    public string? NextRole { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
