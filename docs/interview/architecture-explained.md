# Architecture explained

The first user journey establishes an identity and a private boundary for later issue work. A verified Owner can create a workspace, change appearance, reload, and sign out.

## Follow a request

1. The browser gets an antiforgery cookie and request token through the same-origin Next.js adapter. It supplies the token header on registration, email confirmation, login and other writes.
2. ASP.NET Core Identity hashes the registered password and generates an email confirmation token. The development inbox records the link. The verification screen strips the fragment and submits the proof in the request body.
3. After verifying the password of a confirmed account, Identity issues a host-only, Secure, HttpOnly session cookie. The eight-hour window starts at sign-in. The API rechecks the account and security stamp on authenticated requests.
4. Creating a workspace writes the workspace row and owner membership in one database transaction. PostgreSQL validates the owner membership at commit. Reading a workspace joins it with the caller's current active membership.
5. The UI opens the new workspace URL. Reloading makes fresh API requests using the session. Sign-out clears the cookie and navigates to sign-in.

The account checks add a database read on authenticated requests. Workspace queries join on the caller's ID, and the membership table has an index starting with user ID. The first search and reporting performance measurement belongs to Slice 7.

## Follow an invitation

1. The Owner's request locks the workspace and checks current membership and ownership. The API hashes the random link token, saves the invitation and audit entry, and returns the private link once.
2. The invitee opens the link. The browser strips the fragment and keeps the proof in that tab through sign-in. The preview POST checks the signed-in account's current verified address and invitation state before returning the workspace name and role.
3. Acceptance looks up the matching digest, locks that workspace, and rechecks the recipient and invitation. The membership, consumed invitation and audit entry commit in one transaction.
4. A second acceptance waits for the lock and then returns the existing active membership. A removed invitee needs a separate new invitation. Later workspace requests check current membership using the same session.

## Follow an import page

1. A manual refresh starts or opens a saved run. The API checks the current role and leases the next checkpoint.
2. The API releases the database lock, reads repository metadata and one public GitHub issue page, and ignores pull requests.
3. It takes the workspace lock again and rechecks permission, connection, and lease before writing. It matches issues by stable GitHub ID, writes source snapshots and initial triage records, and advances the checkpoint in one transaction.
4. A completed run updates the repository's last successful refresh. The inbox reads visible snapshots scoped to the caller's workspace and renders GitHub state alongside private triage state.

## Follow a claim

1. The browser sends its command ID and the last decision version it read.
2. The API locks the workspace and rechecks active membership and source visibility. A claim requires Ready and an empty assignment. The assignee comes from the authenticated account.
3. The API saves the assignment, advances the decision version, and appends an activity with the command ID, actor, and time in one transaction.
4. The Member sees the work in **My work**. Start and Done check the current assignee. The Reviewer reads the saved workflow state and completion history alongside the last observed GitHub state.

## Follow a question and answer

A Reviewer assigns a question to an active teammate. The issue becomes Waiting for information and appears in that teammate's My work. On answer, the API verifies the assignment and accepted version inside the workspace transaction. It records the question and answer in history, clears the assignment, and resets the issue's inbox arrival time. An authorized replay can identify the recorded answer even after that assignment has cleared.

## Follow an interrupted refresh

1. The API saves a run cutoff and issues a short lease on the current checkpoint.
2. It reads one GitHub page or a bounded set of canonical issue URLs outside the database transaction.
3. It rechecks authorization, the lease, the checkpoint, and any newer access loss before saving the results and cursor together.
4. A Reviewer can resume from the next checkpoint in a new browser session. The repository's successful cutoff changes when the run finishes.

## Follow a dashboard count

1. The report and inbox use the same workspace, source visibility, and queue predicates in PostgreSQL.
2. Each dashboard link carries the corresponding queue view and, where applicable, assignee. The inbox adds any user filters and applies a stable issue ID tie-breaker.
3. The inbox reads the total and page under one database snapshot. The URL carries the selected detail so Back restores the prior filters and page.
