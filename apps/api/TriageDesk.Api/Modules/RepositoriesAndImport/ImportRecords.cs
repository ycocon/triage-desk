namespace TriageDesk.Api.Modules.RepositoriesAndImport;

public sealed class RepositoryConnection
{
    public Guid WorkspaceId { get; set; }
    public Guid Id { get; set; }
    public long GitHubId { get; set; }
    public required string FullName { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public bool Public { get; set; } = true;
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastSuccessWatermark { get; set; }
}

public sealed class ImportRun
{
    public Guid WorkspaceId { get; set; }
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Guid InitiatorId { get; set; }
    public required string Mode { get; set; }
    public string State { get; set; } = "running";
    public int Checkpoint { get; set; }
    public int NextPage { get; set; } = 1;
    public int Examined { get; set; }
    public int Imported { get; set; }
    public int Updated { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class IssueSnapshot
{
    public Guid WorkspaceId { get; set; }
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public long GitHubId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string State { get; set; } = "open";
    public string[] Labels { get; set; } = [];
    public bool SourceAvailable { get; set; } = true;
    public DateTimeOffset SourceCreatedAt { get; set; }
    public DateTimeOffset SourceUpdatedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}
