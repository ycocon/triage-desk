# Triage Desk

Triage Desk helps a small team turn public GitHub reports into private, assigned work. A Reviewer records a priority and next step. A Member claims the issue, starts work, and marks it Done. The Reviewer sees that completion with the assignee and dated history.

The implementation uses Next.js, ASP.NET Core Identity, Entity Framework Core and PostgreSQL 18. GitHub access uses public reads and manual refresh. Workspace decisions are stored in PostgreSQL.

## Engineering choices

- The workspace transaction checks current membership before committing assignments and decisions. Concurrent claims produce one winner. Command receipts make a lost response safe to retry.
- Refreshes persist a checkpoint with each committed batch. An authorized Reviewer can resume after the original browser tab closes. The successful cutoff advances when the full run completes.
- Canonical source checks confirm visibility loss and transfers. An inaccessible source loses its imported text, while private decisions and history remain attributable.
- Queue reports and issue lists share visibility predicates. Search, filters and selection live in the URL so navigation can restore them.
- The release uses an additive schema migration. The previous API image was exercised against the upgraded database. The restore drill invalidated old sessions and reconciled later membership changes before public-source revalidation.

## Measured evidence

| Check | Observed result | Scope |
| --- | --- | --- |
| API integration | 15 passed | Local PostgreSQL 18 |
| Critical browser matrix | 12 passed | Built web app, Release API, Chromium, Firefox, WebKit |
| Filter latency | 87.1 ms warm p95 | 5,000 synthetic issues, six filters repeated six times |
| Recovery point age | 14.8 seconds | Synthetic local restore rehearsal |
| Detection to safe access | 29.8 seconds | Local images already available |

The filter fixture collected database statistics before measurements. The local recovery time includes source revalidation. Hosted cold-start latency, uptime, real mail delivery and independent alarm delivery still require the hosted environment. CI workflows are present, and hosted CI results await repository publication.

The current candidate was built from an uncommitted working tree. Its source stamp and image digest are recorded in the recovery evidence. Publication needs a reviewed commit and a matching hosted build.

[Browser evidence](evidence/browser-matrix.txt) · [Recovery evidence](evidence/recovery.txt) · [Filter evidence](evidence/filter-performance.txt)

## Product evidence still needed

Human usability is unmeasured. Ask three maintainers to complete a two-minute handoff, record completion time and errors, and revise the flow from their observations.

## Interview introduction

### Thirty seconds

“I built Triage Desk so a small team can review public GitHub issues and track private ownership and next steps. A Reviewer accepts a report, a Member claims it, and the Reviewer can see completion and history. I focused on safe concurrent claims, resumable imports, and recovering private decisions after a database restore.”

### Two minutes

“The core workflow starts with a public issue imported through a manual refresh. The Reviewer decides what should happen next and sets priority. A Member claims the work and records progress. The workspace keeps that decision history, and the GitHub observation appears alongside it.

“I used PostgreSQL transactions to keep authorization, assignment, command receipts and history consistent. Two Members can click Claim at the same time, so the API serializes that decision and returns the saved assignment to the losing request. A repeated command can recover from a lost response without adding duplicate history.

“Imports have a different failure boundary. GitHub calls happen outside the database transaction, and the API checks the lease and checkpoint again before committing. A new Reviewer can resume the same run after the first tab closes.

“I verified the handoff in separate browser sessions and ran critical journeys in three browser engines. I also restored an encrypted database copy, invalidated restored sessions and invitations, reconciled a later membership removal, and rechecked the public source before displaying it again. The next evidence I need is hosted reliability and observation of real maintainers using the handoff.”
