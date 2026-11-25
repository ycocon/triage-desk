using Microsoft.AspNetCore.Identity;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

public sealed class Account : IdentityUser<Guid>
{
    public required string DisplayName { get; set; }
    public string Theme { get; set; } = "system";
    public bool Disabled { get; set; }
}

public sealed class Workspace
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid OwnerUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkspaceMember
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public string BaseRole { get; set; } = "member";
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}
