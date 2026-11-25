# Triage Desk

A private workspace for teams that triage public GitHub issues. The current app supports verified sign-in, team management, public repository connections, manual issue refresh, and the handoff from Reviewer to Member.

## Local setup

You need Node.js 24, the .NET 10 SDK listed in `global.json`, and PostgreSQL 18. The commands below use Docker Compose and PowerShell.

```powershell
docker compose up -d --wait postgres
dotnet tool restore
dotnet build
$env:ConnectionStrings__TriageDesk = 'Host=127.0.0.1;Port=55432;Database=triage_desk;Username=postgres;Password=local-triage-desk'
dotnet ef database update --project apps/api/TriageDesk.Api --no-build
dotnet run --project tools/LocalCertificate.csproj -- .local
npm ci --prefix apps/web
```

A native PostgreSQL 18 installation also works. Create a local database and set the same connection variable to use its host, port, and credentials.

Run the API from the repository root.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'https://127.0.0.1:5100'
$env:Kestrel__Certificates__Default__Path = Join-Path $PWD '.local/localhost.pem'
$env:Kestrel__Certificates__Default__KeyPath = Join-Path $PWD '.local/localhost.key'
$env:ConnectionStrings__TriageDesk = 'Host=127.0.0.1;Port=55432;Database=triage_desk;Username=postgres;Password=local-triage-desk'
$env:Identity__InboxDirectory = Join-Path $PWD '.local/inbox'
$env:Identity__KeyDirectory = Join-Path $PWD '.local/keys'
$env:Identity__WebOrigin = 'https://localhost:3000'
dotnet run --no-launch-profile --project apps/api/TriageDesk.Api
```

Run the web app in a second terminal.

```powershell
Set-Location apps/web
npm run dev -- --hostname 127.0.0.1 --experimental-https --experimental-https-cert ../../.local/localhost.pem --experimental-https-key ../../.local/localhost.key --experimental-https-ca ../../.local/localhost-ca.pem
```

Open [local Triage Desk](https://localhost:3000) and accept the local certificate in your browser. Register, open the matching JSON message in `.local/inbox`, and paste its `url` into the browser to confirm your address. The inbox and keys contain local credentials and are ignored by Git. Use a synthetic email address. Hosted email delivery is planned for the release slice.

Open a workspace and select **Team**. Create an invitation for a specific email and choose Member or Reviewer. Copy the private link and open it in a separate browser session. Sign in or verify a new account with that email in the tab where you opened the link, then accept the invitation. The link expires after seven days. The Owner can change roles, revoke pending links, remove teammates, and transfer ownership before leaving.

As Owner, open **Repositories**, enter `owner/repository`, preview the public repository, and connect it. An Owner or Reviewer can select **Refresh issues**. The screen shows the examined and imported counts and the last successful refresh. Open **Triage inbox** to see the reports in **Needs review** and select one for its GitHub details. Members can read the inbox and issue detail. Disconnecting hides the source from the inbox and preserves the workspace's triage record.

As Reviewer, accept an issue with a priority and next step. As Member, select **Available to claim**, claim the issue, then open **My work** to start it and mark it **Done**. In the Reviewer session, reload and select **All issues → Done** to see the workspace status, GitHub observation, assignee, and dated decision history.

## Verify the slice

Stop the manually started API and web servers before running Playwright. It starts the API, Next.js and a local GitHub fixture that serves two pages for `northline/cli`. Keep PostgreSQL running. In the terminal used for API tests, set `ConnectionStrings__TriageDesk` to the migrated local database. If you use `TRIAGEDESK_EXTERNAL_SERVERS=1`, run `node e2e/github-fixture.mjs` from `apps/web` and set `GitHub__FixtureOrigin=http://127.0.0.1:5101` on your Development API.

```powershell
dotnet test
npm run typecheck --prefix apps/web
npm run build --prefix apps/web
Set-Location apps/web
npx playwright install chromium
npm run test:e2e
```

The browser tests cover sign-in and private workspaces, team access, public issue import, and the Member-to-Reviewer handoff. The handoff uses separate authenticated browser sessions and checks for zero GitHub requests during the workflow. The PostgreSQL integration tests exercise import rollback and replay, competing claims, and returning unfinished work when the assignee leaves.

## Architecture

```text
Browser over HTTPS → Next.js /api/v1 adapter → ASP.NET Core Identity and workspace endpoints → PostgreSQL 18
```

Next.js relays the API's host-only session and antiforgery cookies. The API verifies the session and looks up the caller's current workspace membership. The first migration adds a deferred owner-membership foreign key so PostgreSQL enforces that each workspace has a corresponding owner membership when creation commits.

Team writes lock the workspace row, check current authorization, and commit membership or invitation changes with their audit entry. The database stores invitation digests and enforces one pending invitation per normalized email in a workspace. The API hides old invitations after the retention window and purges aged rows when issuing invitations in that workspace.

Manual refresh fetches one page at a time, after checking the public repository identity. Each page, new triage records, counts, and its saved checkpoint commit together. A first-seen open report enters **Needs review**. Slice 3 scans open issues. Later slices add incremental reconciliation for source changes and the full issue search and filter contract.

The API serializes workspace decisions with membership changes. It checks the expected version and stores the command, decision version, and history in one transaction. A matching authorized replay returns the current triage.

The full planned v1 contract is in `contracts/openapi/v1.json`. Slices 1–4 cover 35 operations, including issue claims, the initial decision commands, and per-issue history. Regenerate the web transport types with `npm ci --prefix contracts` followed by `npm run generate --prefix contracts`. The generator uses its own pinned TypeScript version. The web app builds with TypeScript 7.

See [the interview notes](docs/interview/project-overview.md), [request flow](docs/interview/architecture-explained.md), and [demo script](docs/interview/demo-script.md).
