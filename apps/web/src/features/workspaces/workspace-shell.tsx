"use client";

import Link from "next/link";
import { useEffect, useState, type FormEvent } from "react";
import { Brand } from "@/features/account/brand";
import { ApiError, api, messageFor, type Schema } from "@/lib/http";
import { TeamPanel } from "./team-panel";
import { RepositoryPanel } from "./repository-panel";
import { InboxPanel } from "./inbox-panel";
import { WorkspaceOverview } from "./workspace-overview";
import { WorkspaceActivity, type ActivityQuery } from "./workspace-activity";
import { ClosedAssignmentsNotice } from "./closed-assignments-notice";
import type { InboxQuery } from "./inbox-query";

type Loaded = { account: Schema["User"]; page: Schema["WorkspacePage"]; workspace?: Schema["Workspace"] };

export function WorkspaceShell({ workspaceId, view = "overview", inboxQuery, activityQuery }: {
  workspaceId?: string; view?: "overview" | "team" | "repositories" | "inbox" | "activity";
  inboxQuery?: InboxQuery; activityQuery?: ActivityQuery;
}) {
  const [loaded, setLoaded] = useState<Loaded>();
  const [page, setPage] = useState(1);
  const [error, setError] = useState("");
  const [actionError, setActionError] = useState("");
  const [pending, setPending] = useState(false);
  const [creating, setCreating] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [issueRevision, setIssueRevision] = useState(0);
  const issueChanged = () => setIssueRevision(value => value + 1);

  useEffect(() => {
    const controller = new AbortController();
    const options = { signal: controller.signal };
    const onRestore = (event: PageTransitionEvent) => {
      if (event.persisted) window.location.reload();
    };
    window.addEventListener("pageshow", onRestore);
    async function load() {
      try {
        const [account, list, workspace] = await Promise.all([
          api<Schema["User"]>("/me", options),
          api<Schema["WorkspacePage"]>("/workspaces?page=" + page, options),
          workspaceId ? api<Schema["Workspace"]>("/workspaces/" + encodeURIComponent(workspaceId), options) : undefined,
        ]);
        if (!controller.signal.aborted) setLoaded({ account, page: list, workspace });
      } catch (failure) {
        if (controller.signal.aborted) return;
        setLoaded(undefined);
        if (failure instanceof ApiError && failure.status === 401) window.location.replace("/sign-in");
        else setError(messageFor(failure));
      }
    }
    void load();
    return () => {
      controller.abort();
      window.removeEventListener("pageshow", onRestore);
    };
  }, [workspaceId, page, refresh]);

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const name = String(new FormData(event.currentTarget).get("name") ?? "").trim();
    if (!name) {
      setActionError("Enter a workspace name.");
      return;
    }
    await run(async () => {
      try {
        const body: Schema["CreateWorkspace"] = { name };
        const workspace = await api<Schema["Workspace"]>("/workspaces", {
          method: "POST", body: JSON.stringify(body),
        });
        window.location.assign("/workspaces/" + workspace.workspaceId);
      } catch (error) {
        setRefresh(value => value + 1);
        throw error;
      }
    });
  }

  async function run(action: () => Promise<void>) {
    if (pending) return;
    setPending(true);
    setActionError("");
    try {
      await action();
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 401) {
        setLoaded(undefined);
        window.location.replace("/sign-in");
      } else setActionError(messageFor(failure));
    } finally {
      setPending(false);
    }
  }

  if (!loaded) {
    return <main className="standalone"><Brand />
      {error ? <><h1>Workspace unavailable</h1><p role="alert">{error}</p><Link href="/workspaces">Back to workspaces</Link></> :
        <p role="status">Loading workspaces…</p>}
    </main>;
  }

  const { account, workspace, page: list } = loaded;
  const workspacePath = workspace && "/workspaces/" + workspace.workspaceId;
  return (
    <div className="workspace-layout" data-theme={account.theme}>
      <a className="skip-link" href="#content">Skip to content</a>
      <aside className="sidebar">
        <Brand />
        <nav aria-label="Workspace navigation">
          <Link className={!workspace ? "active" : ""} href="/workspaces" aria-current={!workspace ? "page" : undefined}>
            <span className="nav-icon" aria-hidden="true">▤</span> Workspaces
          </Link>
          {workspace && workspacePath && <>
            <Link className={view === "overview" ? "active" : ""} aria-current={view === "overview" ? "page" : undefined} href={workspacePath}>
              <span className="workspace-initial small" aria-hidden="true">{workspace.name.slice(0, 1)}</span>{workspace.name}
            </Link>
            <Link className={view === "team" ? "active" : ""} aria-current={view === "team" ? "page" : undefined} href={workspacePath + "/team"}>
              <span className="nav-icon" aria-hidden="true">◈</span> Team
            </Link>
            <Link className={view === "repositories" ? "active" : ""} aria-current={view === "repositories" ? "page" : undefined} href={workspacePath + "/repositories"}>
              <span className="nav-icon" aria-hidden="true">▦</span> Repositories
            </Link>
            <Link className={view === "inbox" ? "active" : ""} aria-current={view === "inbox" ? "page" : undefined} href={workspacePath + "/inbox"}>
              <span className="nav-icon" aria-hidden="true">◎</span> Triage inbox
            </Link>
            <Link className={view === "activity" ? "active" : ""} aria-current={view === "activity" ? "page" : undefined} href={workspacePath + "/activity"}>
              <span className="nav-icon" aria-hidden="true">◷</span> Activity
            </Link>
          </>}
        </nav>
        <div className="account-summary">
          <span className="avatar" aria-hidden="true">{account.displayName.slice(0, 1)}</span>
          <span><strong>{account.displayName}</strong><small>{account.email}</small></span>
        </div>
      </aside>
      <div className="workspace-panel">
        <header className="topbar">
          <div className="breadcrumb"><Link href="/workspaces">Workspaces</Link>{workspace && <> <span aria-hidden="true">/</span> <strong>{workspace.name}</strong></>}
            {view !== "overview" && workspacePath && <> <span aria-hidden="true">/</span> <Link href={workspacePath}>Overview</Link></>}
          </div>
          <div className="account-controls">
            <label>Appearance <select value={account.theme} disabled={pending} onChange={event => {
              const theme = event.target.value as Schema["Preferences"]["theme"];
              void run(async () => {
                const updated = await api<Schema["User"]>("/me/preferences", {
                  method: "PATCH", body: JSON.stringify({ theme }),
                });
                setLoaded(current => current && { ...current, account: updated });
              });
            }}>
              <option value="system">System</option><option value="light">Light</option><option value="dark">Dark</option>
            </select></label>
            <button disabled={pending} onClick={() => void run(async () => {
              await api("/auth/logout", { method: "POST" });
              setLoaded(undefined);
              window.location.replace("/sign-in");
            })}>Sign out</button>
          </div>
        </header>
        <main id="content" className="workspace-content" tabIndex={-1}>
          {workspacePath && <ClosedAssignmentsNotice key={workspacePath} path={workspacePath} revision={issueRevision} />}
          {workspace && view === "team" ? <TeamPanel workspace={workspace} account={account} /> :
            workspace && view === "repositories" ? <RepositoryPanel workspace={workspace} onIssuesChanged={issueChanged} /> :
            workspace && view === "inbox" && inboxQuery ?
              <InboxPanel workspace={workspace} account={account} filters={inboxQuery} onIssuesChanged={issueChanged} /> :
            workspacePath && view === "activity" && activityQuery ? <WorkspaceActivity path={workspacePath} filters={activityQuery} /> :
            workspace ? <WorkspaceOverview workspace={workspace} /> : (
            <>
              <div className="list-heading"><div><h1>Your workspaces</h1><p className="hint">Open a workspace or create one for your team.</p></div>
                {!creating && <button className="primary" onClick={() => setCreating(true)}>New workspace</button>}
              </div>
              {creating && <form className="create-form" onSubmit={create} aria-busy={pending}>
                <label>Workspace name<input name="name" autoFocus required maxLength={80} /></label>
                <div className="form-actions">
                  <button className="primary" disabled={pending}>{pending ? "Creating…" : "Create workspace"}</button>
                  <button type="button" disabled={pending} onClick={() => { setCreating(false); setActionError(""); }}>Cancel</button>
                </div>
              </form>}
              {list.items.length ? <ul className="workspace-list">
                {list.items.map(item => <li key={item.workspaceId}><Link href={"/workspaces/" + item.workspaceId}>
                  <span className="workspace-initial small" aria-hidden="true">{item.name.slice(0, 1)}</span>
                  <strong>{item.name}</strong><span className="role">{item.myRole}</span><span aria-hidden="true">›</span>
                </Link></li>)}
              </ul> : !creating && <div className="empty-state"><h2>No workspaces yet</h2><p>Create a workspace to get started.</p></div>}
              {list.total > list.pageSize && <nav className="pages" aria-label="Workspace pages">
                <button disabled={page === 1} onClick={() => setPage(page - 1)}>Previous</button>
                <span>Page {list.page}</span>
                <button disabled={page * list.pageSize >= list.total} onClick={() => setPage(page + 1)}>Next</button>
              </nav>}
            </>
          )}
          {actionError && <p role="alert" className="error">{actionError}</p>}
        </main>
      </div>
    </div>
  );
}
