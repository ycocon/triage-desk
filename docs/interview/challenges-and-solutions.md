# Challenges and solutions

## Reproducing real HTTPS locally

The first Playwright run reached Kestrel, where antiforgery rejected plain HTTP because the cookie requires HTTPS. Enabling TLS exposed a second problem. The Next.js dev launcher passes its own certificate authority configuration to the route-handler process. The local certificate tool now generates a development CA and a localhost certificate, and the Next.js command supplies the CA explicitly. Both server hops use TLS.

## Keeping the transport generator compatible

The OpenAPI generator declares a TypeScript 5 peer dependency. The approved web baseline uses TypeScript 7. Nesting the generator in the contract package with its own lockfile lets CI regenerate and compare transport types while the application keeps its approved compiler.

## Querying a newly created workspace

The first browser journey could commit a workspace but failed to read it. EF Core could not apply the list sort and detail filter to the constructor projection. An explicit member projection lets the provider translate those operations into SQL. The browser test now reads both the workspace list and detail after creating a row.

## Recovering from an ambiguous create

The database commits the workspace and its first membership together. If the browser loses the response, it fetches the workspace list again before the user decides whether to resubmit.

## Reissuing an expired invitation

The partial unique index counts pending state, while expiry also depends on the current time. Reissue marks a stale invitation expired before inserting its replacement in the same transaction. If the transaction fails, both changes roll back. Pending lists calculate expiry at read time so an old link is shown as expired even before any reissue.

## Repeated reports across GitHub pages

The import fixture repeats a report on page two and includes a pull request on page one. The page parser skips pull requests. A stable GitHub ID uniquely identifies a snapshot in the workspace. An interrupted transaction leaves both the issue rows and the checkpoint unchanged, so the same page can be retried.

## Two Members claim the same report

The PostgreSQL test sends two claims using the same expected version and different accounts. One claim commits and receives Ready with its assignment. The other receives a conflict with the saved assignment. Replaying the winning command leaves one claim activity.

## Keeping command replay compatible after adding new fields

Slice 5 adds question, answer, severity, and reason fields to commands. Older receipts store a digest of the original payload shape. The digest omits the added fields when they are absent so a Slice 4 command still matches the value saved before those fields existed. The integration test seeds a receipt in the old format before replaying it through the updated API.

## An issue disappears from a GitHub page

Moving page boundaries and `since` filtering can both omit a valid report. The refresh checks the canonical issue URL before recording an access loss. A timeout keeps the saved snapshot available and pauses the run. Confirmed loss scrubs imported text and labels in every known affected workspace. The API checks loss records before committing in-flight observations, so an older response cannot bring back scrubbed content.

## Measuring filters on a populated workspace

The first bulk-seeded run showed a nested loop over millions of row combinations because PostgreSQL had stale row estimates. After `ANALYZE` on the fixture tables, PostgreSQL used a hash join for the tested queue count. The repeatable benchmark records the first request per query shape and repeated requests after fixture setup. It measures API response time with PostgreSQL statistics available.
