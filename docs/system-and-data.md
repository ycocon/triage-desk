# System and data model

```mermaid
flowchart LR
  Browser[Browser] --> Web[Next.js same-origin adapter]
  Web --> API[ASP.NET Core API]
  API --> DB[(PostgreSQL 18)]
  API --> GitHub[Public GitHub REST reads]
  API --> Mail[Verification mail provider]
  Backup[Read-only export job] --> DB
  Backup --> Archive[Encrypted independent archive]
```

The adapter forwards the session and antiforgery proof to a fixed API origin. The API checks the authenticated account and current workspace membership. GitHub reads happen during authorized manual refreshes. The database owns private workflow decisions and history.

```mermaid
erDiagram
  Account ||--o{ WorkspaceMember : joins
  Workspace ||--o{ WorkspaceMember : contains
  Workspace ||--o{ WorkspaceInvitation : invites
  Workspace ||--o{ TeamAuditEntry : records
  Workspace ||--o{ RepositoryConnection : connects
  RepositoryConnection ||--o{ ImportRun : refreshes
  RepositoryConnection ||--o{ IssueSnapshot : imports
  IssueSnapshot ||--|| IssueTriage : decides
  IssueSnapshot ||--o{ IssueActivity : records
  IssueSnapshot ||--o{ IssueExternalKey : identifies
```

Workspace-owned relationships use workspace-scoped keys. The stable local issue ID preserves history through a verified upstream transfer. Protection keys are certificate-wrapped records shared by API instances. Source-access-loss records fence older source observations across workspaces.

[Architecture walkthrough](interview/architecture-explained.md) · [Technical decisions](interview/technical-decisions.md) · [Recovery runbook](runbooks/release-and-recovery.md)
