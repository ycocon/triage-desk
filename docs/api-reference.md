# API reference

Browser calls use the same-origin /api/v1 adapter and host-only session cookie. Mutations require the request token from GET /auth/csrf in X-CSRF-Token.

The [OpenAPI contract](../contracts/openapi/v1.json) defines payloads, response schemas and errors. [Generated transport types](../apps/web/src/lib/api-types.ts) come from that contract.

| Method | Path | Operation |
| --- | --- | --- |
| GET | /auth/csrf | Get a request token |
| POST | /auth/register | Register an account |
| POST | /auth/email-verification/requests | Request another verification email |
| POST | /auth/email-verification | Confirm email ownership |
| POST | /auth/login | Open a browser session |
| POST | /auth/logout | End the browser session |
| GET | /me | Read current account |
| PATCH | /me/preferences | Set personal appearance |
| GET | /workspaces | List my workspaces |
| POST | /workspaces | Create a workspace |
| GET | /workspaces/{workspaceId} | Read workspace |
| GET | /workspaces/{workspaceId}/members | List active members |
| PATCH | /workspaces/{workspaceId}/members/{userId} | Change a member role |
| DELETE | /workspaces/{workspaceId}/members/{userId} | Remove a teammate |
| DELETE | /workspaces/{workspaceId}/membership | Leave the workspace |
| PUT | /workspaces/{workspaceId}/ownership | Transfer workspace ownership |
| GET | /workspaces/{workspaceId}/invitations | List workspace invitations |
| POST | /workspaces/{workspaceId}/invitations | Invite a teammate |
| DELETE | /workspaces/{workspaceId}/invitations/{invitationId} | Revoke a pending invitation |
| POST | /invitations/accept | Join using an invitation |
| POST | /invitations/preview | Read the invited team and role |
| POST | /workspaces/{workspaceId}/repository-previews | Preview a public repository |
| GET | /workspaces/{workspaceId}/repositories | List repository connections |
| POST | /workspaces/{workspaceId}/repositories | Connect or reconnect a repository |
| GET | /workspaces/{workspaceId}/repositories/{repositoryId} | Read connection and freshness |
| DELETE | /workspaces/{workspaceId}/repositories/{repositoryId} | Disconnect a repository |
| POST | /workspaces/{workspaceId}/repositories/{repositoryId}/sync-runs | Start a manual refresh |
| GET | /workspaces/{workspaceId}/repositories/{repositoryId}/sync-runs/{runId} | Read saved refresh progress |
| POST | /workspaces/{workspaceId}/repositories/{repositoryId}/sync-runs/{runId}/advance | Advance one saved batch |
| POST | /workspaces/{workspaceId}/repositories/{repositoryId}/sync-runs/{runId}/pause | Pause at a batch boundary |
| GET | /workspaces/{workspaceId}/issues | Search and filter issues |
| GET | /workspaces/{workspaceId}/issues/{issueId} | Read one issue and its workspace decision |
| POST | /workspaces/{workspaceId}/issues/{issueId}/claim | Claim available work for myself |
| POST | /workspaces/{workspaceId}/issues/{issueId}/commands | Apply one workspace decision |
| GET | /workspaces/{workspaceId}/issues/{issueId}/activity | Read issue history |
| GET | /workspaces/{workspaceId}/activity | Read workspace issue activity |
| GET | /workspaces/{workspaceId}/reports/overview | Get queue counts and oldest report |
| GET | /workspaces/{workspaceId}/reports/workload | Get active workload per assignee |

Operational probes use /health/live and /health/ready outside the browser API contract.
