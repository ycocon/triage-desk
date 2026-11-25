# Demo script

## Slice 1

1. Register a synthetic account and open the verification link from the local inbox.
2. Sign in, create **Northline Tools**, set Dark appearance, and reload the workspace.
3. Open its URL in another verified account. Show the unavailable screen and explain the 404.
4. Return to the Owner session, sign out, and show that visiting the workspace URL redirects to sign-in.
5. Show the automated browser test and point out the session-expiry integration test.

## Slice 2

1. Open **Team** as the Owner. Invite Mara as a Reviewer and Jules as a Member.
2. Open each link in a separate browser context. Confirm the invited email and accept to show the assigned role.
3. Change Jules's role and then remove Jules. Reload the already-open Team page from that account to show the loss of access.
4. Transfer ownership to Mara. Show the Owner controls in Mara's session after reload, then leave from the former Owner session.
5. Explain the seven-day boundary, repeat acceptance and transfer-versus-removal tests. Keep the invitation proof off screen once it's served its purpose.

## Slice 3

1. Preview and connect a public repository from the Owner view. The local test fixture offers `northline/cli`.
2. Open **Repositories** as Reviewer and refresh. Point out the examined count, imported count, and the last successful refresh.
3. Open the **Triage inbox**, select a report, and point out the **Needs review** and **GitHub Open** labels.
4. Show the Member's read access, then explain the page, checkpoint, and rollback test.

## Slice 4

1. As Mara, accept **CLI crashes on Windows** with High priority and a concrete next step.
2. As Jules in a separate session, open **Available to claim**, read the next step, and claim the issue. Use **My work** to start it and mark it **Done**.
3. Reload Mara's session. Open **All issues → Done** and show Done, GitHub Open, Jules's assignment, and the dated completion entry.
4. Explain how the transaction resolves two competing claims and how the command ID makes a lost response safe to retry.
