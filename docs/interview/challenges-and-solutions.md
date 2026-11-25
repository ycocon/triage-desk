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
