# Release, rollback and recovery

## Environments

The approved hosted profile places the Vercel Node.js function in `sin1`, the Render image-backed API in Singapore, and PostgreSQL 18 in Neon Singapore. Use isolated accounts, databases, domains and environments during a rehearsal. The public domains belong to the publication slice.

| Component | Rehearsal configuration |
| --- | --- |
| GitHub | Reviewed commit on `main`, distinct `rehearsal-release` and `backup` environments, and a private API image package. Grant the Render pull credential read access to that package. |
| Vercel | Set the root to `apps/web` and the server-only `TRIAGEDESK_API_ORIGIN` to the fixed HTTPS rehearsal API. Use dedicated token, organization and project settings. Keep the Git production deployment disabled while Actions owns release order. |
| Render | Use the image-backed service, port 10000, and health path `/health/live`. Set the public web origin, a verified Resend sender, the release revision and the wrapping certificate credentials in the service. The candidate image sets the revision at build time. |
| Neon | Use a verified TLS pooled connection for the runtime role with maximum pool size 10 and minimum 0. Use direct verified TLS connections for the migration, backup and restore roles. |
| Cloudflare R2 | Create a private Standard bucket and keep public access disabled. Use separate writer and release reader credentials scoped to the bucket. Keep the age private key outside GitHub and R2. |

Provider references for the release operator. [Vercel function placement](https://vercel.com/docs/functions/configuring-functions/region) · [Render image-backed services](https://render.com/docs/deploying-an-image) · [Neon pooling](https://neon.com/docs/connect/connection-pooling)

Create the owner, migration, runtime and read-only backup identities before the first migration. The migration identity owns app tables. Apply `ops/database/roles.sql` as the database owner after schema changes. Keep migration credentials out of the API service. The runtime may append audit entries and protection keys and read migration history. In PostgreSQL, protect stored Data Protection XML with the API's X.509 wrapping certificate. Back up its private key independently. During certificate rotation provide the previous certificate until retained sessions and tokens are past their validity period.

## Release and rollback

For initial hosting setup, dispatch **Publish verified API image** after CI passes for the same `main` revision. It publishes the API candidate to GHCR and records its immutable digest in the workflow summary. It does not migrate a database or deploy a service. Use that digest when preparing the first image-backed Render service, then complete the hosted release checks below before assigning public domains.

1. Verify the build, API contract, database migration and critical browser matrix for the reviewed commit. Confirm the isolated database and verify the ciphertext named by a recent backup manifest.
2. Dispatch **Rehearse an isolated release**. It builds the API, records the immutable registry digest, applies the reviewed EF migration bundle with the direct identity, and deploys that digest to Render.
3. Check `/health/ready` and the `X-TriageDesk-Release` response header. Readiness checks the PostgreSQL version and the minimum schema migration. Do a synthetic sign-in and read an existing issue with the previous web client.
4. Deploy the web build to its isolated generated URL. Record the Git commit, migration, API digest, Vercel deployment ID and readiness evidence before any domain assignment.
5. If the API or web release fails, select the preceding digest or deployment using the provider. Keep a compatible additive migration in place. Verify that a Reviewer can still read a stored decision and history, and a disconnected source stays outside active lists.

For the first rollout of the health endpoints, keep the older service's working probe until the rollback image also supports `/health/live`. The local predecessor was probed at `/api/v1/auth/csrf`. A rollback to a build predating the PostgreSQL key store requires users to sign in again.

[Render digest deploy hooks](https://render.com/docs/deploy-hooks) · [EF migration bundles](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) · [Vercel rollback](https://vercel.com/docs/instant-rollback)

## Backup and alerts

- The backup workflow runs at 04:23 and 16:23 UTC, or 12:23 p.m. and 12:23 a.m. in Manila. It exports through the direct endpoint, validates the PostgreSQL archive, encrypts before upload, and verifies the uploaded ciphertext hash before writing a manifest and sending the success heartbeat.
- Apply the seven-day lock and lifecycle prefix from `ops/r2-bucket-lock.json` and `ops/r2-lifecycle.json`. Verify that a new object cannot be deleted by the writer. Keep the retention-management credential separate.
- Configure an independent Healthchecks.io check for the same cron with a six-hour grace period. Test a missed ping and a failed-job ping with a separate rehearsal check and confirm operator delivery. Escalate when the last verified source snapshot reaches 24 hours. Inspect its timestamp rather than relying on upload time.
- Send structured admin events to an independent restricted log destination, for example Axiom where supported by the account. Permit the operator and restore verifier to read them. Retain them beyond seven days of backup history. Restrict write and settings privileges independently. Events contain action, IDs, UTC time, trace and release. Verify ingestion and retrieval by event ID, including a committed membership removal.

[R2 bucket locks](https://developers.cloudflare.com/r2/buckets/bucket-locks/) · [R2 lifecycle settings](https://developers.cloudflare.com/r2/buckets/object-lifecycles/) · [Healthchecks schedule syntax](https://healthchecks.io/docs/cron/)

## Restore and reopen

1. Keep traffic paused. Choose a recent manifest, verify its ciphertext hash, retrieve the age identity from independent custody, decrypt, and verify the archive hash. Record the decision time and the source snapshot time.
2. Restore to a new PostgreSQL 18 database using the direct restore identity. Use `pg_restore --exit-on-error --single-transaction --no-owner --no-privileges` and the isolated migration owner. Reapply the approved role grants from `ops/database/roles.sql`.
3. With application processes stopped, run `ops/database/quarantine-restore.sql`. The script rotates session stamps, clears old protection keys, revokes pending invitations, cancels open runs, and masks imported GitHub content. Start the API with `Operations__RecoveryMode=true` for checks. Liveness remains available, readiness returns 503, and mutations are blocked.
4. Reconcile membership removals, role changes, ownership transfers, account revocations and repository disconnects since the snapshot with independent logs and account-owner verification. Apply retirement logic for active assignments when replaying membership changes. Keep traffic paused if an affected workspace's access cannot be verified. Reissue invitations after approval.
5. Enable the API only on an isolated origin, verify revoked accounts and invites, and manually recheck sources before bringing the restored workspace back. Verify that a Reviewer can read the private decision and its history and that unavailable source text stays hidden. Record the time and snapshot age. The targets are four hours to verified reviewer access and at most 24 hours of data loss.

The operator should repeat the exercise during the first working week of each month. Start with a selected retained copy and record the tested manifest, elapsed time, observed data loss, access decisions and smoke results. Use a fresh synthetic workspace for each hosted drill.

## Local proof

`ops/rehearse-local.ps1` uses disposable Docker resources, PostgreSQL 18 client tools and age. Build the previous committed API and current candidate with `apps/api/Dockerfile`, tag them `triage-desk-slice8-previous:local` and `triage-desk-slice8-candidate:local`, and save the candidate build revision in `.local/slice-8-source-stamp.txt`. Build the .NET solution and generate the local TLS certs as described in the README. Supply the directories containing the native PostgreSQL tools and age executables, and an output path for the non-secret evidence.

For the critical browser matrix, build Next.js and the API in Release configuration, migrate a disposable database, set `TRIAGEDESK_RELEASE_TEST=1`, and run Playwright for Chromium, Firefox and WebKit in separate invocations. The harness starts the built Next.js app over local HTTPS and uses synthetic mail and GitHub fixtures.
