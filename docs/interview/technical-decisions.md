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
