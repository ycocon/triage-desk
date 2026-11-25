# Interview questions

## Slice 1 knowledge check

Why must the API check workspace membership again when it already has a valid session cookie?

Try answering in terms of what the cookie proves and what could happen between sign-in and the next workspace request.

## Practice

- What database guarantee prevents a workspace from committing without an owner membership?
- What does the antiforgery token add to the session cookie?
- How does the integration test show the eight-hour deadline remains fixed when a session is used?

## Slice 2 knowledge check

Which checks must happen together when an invitation is accepted?

Consider the invitee's verified address, what can change while the request waits for the workspace lock, and what a retry should see.

### Practice

- How can an Owner replace an expired invitation when a partial unique index allows one pending invitation per email?
- What are the two possible outcomes when ownership transfer and removal target the same teammate?
- Why does an old acceptance link fail to restore a retired membership?

## Slice 3 knowledge check

Why save the imported issue rows and the next page checkpoint in the same transaction?

- What happens if the server writes the issues and loses power before saving the checkpoint?
- Why does the importer use a GitHub ID instead of the issue number?
- Which checks must run again after the request to GitHub comes back?

## Slice 4 knowledge check

If Mark Done succeeds but the response is lost, what can the command ID tell the API that the issue version cannot?

## Slice 5 knowledge check

If a teammate saves a change while you are drafting a new next step, how can the app show you their saved change and keep your draft?

## Slice 6 knowledge check

If an issue disappears from a GitHub list page, what should the app check before hiding it?

## Slice 7 knowledge check

Why should the dashboard count and the list it opens use the same visibility rules?

## Slice 8 knowledge check

Why can restoring yesterday’s database bring back access that was revoked today?
