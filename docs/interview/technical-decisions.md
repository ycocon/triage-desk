# Technical decisions

## Same-origin adapter

Next.js relays a small set of headers and host-only cookies to a fixed API origin. ASP.NET Core Identity issues the session and the API authorizes workspace operations. A cross-origin browser API would add browser credential and CORS configuration. The adapter adds a server network hop.

## Session tickets and current membership

An eight-hour cookie ticket identifies the account. The API checks current membership and the security stamp when serving authenticated operations. A browser sign-out expires its cookie, and changing the security stamp revokes outstanding tickets. A per-device session store could add finer revocation at the cost of managing each session's state.

## Atomic workspace creation

The workspace points to its owner and has a corresponding membership row. PostgreSQL checks the owner foreign key at commit, so both rows can be inserted in the same transaction. Independent writes could leave an unusable workspace if membership creation failed.

## Invitation proof and ownership races

The invitation combines a high-entropy token with a verified email match. A self-contained signed invitation could prove issuance and expiry. Stored state also supports targeted revocation, a pending-invitation uniqueness constraint, and atomic consumption and replay.

Team writes lock the workspace row before reading memberships. This serializes competing administration within a workspace. It keeps a transfer from selecting a candidate whose removal has already committed. Short transactions limit contention, and requests for other workspaces take different locks.

## A short lease with a durable checkpoint

The import uses a short lock to reserve a page, then makes the GitHub request outside the transaction. A long transaction across the network would hold the workspace lock during delays. The second transaction checks that the lease still belongs to this request and saves the page with the checkpoint. A retry using an already committed checkpoint receives saved progress without repeating the read.

## Command identity and decision versions

A command ID identifies an attempted action. A matching actor and payload can replay it without writing another history entry. The expected version identifies the state the caller saw. A new command based on an older version receives current triage with a conflict response.

The workspace lock also serializes membership removal with claims. A removal that commits first prevents the claim. A claim that commits first is retired with the membership and returns the issue to Needs review with history. The workspace stays locked for a short database transaction. A narrower lock design would need a consistent order for the issue and membership rows.

## Stale drafts

The API returns the current triage and version in a 409 response. The form keeps its unsaved values while the detail updates to show the saved decision. A deliberate second save starts a new command against that version. Automatically resending the draft against a new version could overwrite work the author hasn't reviewed.

## Refresh cutoff and source identity

Incremental runs use a five-minute overlap and a fixed start cutoff. Each page or canonical recheck batch has an expiring lease. The app honors GitHub retry times and uses bounded backoff for temporary failures. A process-wide request budget shares unauthenticated capacity across repositories and discovery calls.

Workspace-local issue IDs and private decisions survive a verified redirect to a connected public repository. The app retains the old GitHub identifier as an alias. When a destination issue already has its own record, both histories stay intact and both issues get a transfer-conflict flag. A short PostgreSQL transaction lock serializes source writes that can affect copies across workspaces.

## Queue reports and drill-downs

The inbox, overview, and workload report call the same visibility and queue queries. A count and its drill-down use the same rules across connected and disconnected sources. Separate requests can still observe changes made by teammates. A single paginated response uses repeatable read so its total and rows agree.

The assignment notice reads the viewer's active assignments filtered to the latest GitHub Closed state. It opens that filtered queue and refreshes on page load, tab focus, and local issue changes.

## Release compatibility and recovery

The candidate checks a minimum database migration at readiness. The previous image still reads the upgraded schema after rollback. PostgreSQL stores certificate-wrapped Data Protection keys for the running API. A restored older database starts in recovery mode. Session rotation, invitation revocation, cancellation of saved refreshes, and reconciliation against later admin events precede reopening.
