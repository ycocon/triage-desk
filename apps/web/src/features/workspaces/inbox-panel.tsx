"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { api, messageFor, type Schema } from "@/lib/http";
import { IssueWorkspaceDetail, issueIdentifier, sourceState, statusLabel } from "./issue-workspace-detail";

export type InboxView = "needsReview" | "available" | "myWork" | "all";
const views = {
  needsReview: { label: "Needs review", empty: "No issues need review. Refresh a connected repository to import open issues." },
  available: { label: "Available to claim", empty: "No issues available to claim." },
  myWork: { label: "My work", empty: "No active assignments." },
  all: { label: "All issues", empty: "No issues match this view." },
};

export function InboxPanel({ workspace, account, issueId, page, view, done }: {
  workspace: Schema["Workspace"]; account: Schema["User"]; issueId?: string; page: number; view: InboxView; done: boolean;
}) {
  const path = "/workspaces/" + workspace.workspaceId;
  const inbox = path + "/inbox";
  const filter = done ? "&status=done" : "";
  const query = "view=" + view + filter + "&page=" + page;
  const [result, setResult] = useState<{ query: string; page: Schema["IssueSummaryPage"] }>();
  const [detail, setDetail] = useState<{ key: string; issue: Schema["IssueDetail"] }>();
  const [listError, setListError] = useState("");
  const [detailError, setDetailError] = useState<{ id: string; message: string }>();
  const [revision, setRevision] = useState(0);
  const detailKey = query + "&issue=" + issueId;

  useEffect(() => {
    const controller = new AbortController();
    api<Schema["IssueSummaryPage"]>(path + "/issues?" + query, { signal: controller.signal })
      .then(items => {
        if (controller.signal.aborted) return;
        setResult({ query, page: items });
        setListError("");
      }).catch(failure => { if (!controller.signal.aborted) setListError(messageFor(failure)); });
    return () => controller.abort();
  }, [path, query, revision]);

  useEffect(() => {
    if (!issueId) return;
    const controller = new AbortController();
    api<Schema["IssueDetail"]>(path + "/issues/" + encodeURIComponent(issueId),
      { signal: controller.signal }).then(issue => {
        if (controller.signal.aborted) return;
        setDetail({ key: detailKey, issue });
        setDetailError(undefined);
      }).catch(failure => {
        if (!controller.signal.aborted) setDetailError({ id: issueId, message: messageFor(failure) });
      });
    return () => controller.abort();
  }, [path, issueId, detailKey]);

  const selected = detail?.key === detailKey ? detail.issue : undefined;
  const list = result?.query === query ? result.page : undefined;
  const listName = done ? "Done" : views[view].label;
  return <>
    <div className="list-heading"><div><h1>Triage inbox</h1><p className="hint">Review, claim, and track workspace issues.</p></div>
      <Link href={path + "/repositories"}>View repositories</Link></div>
    <nav className="issue-nav" aria-label="Issue views">
      {(Object.keys(views) as InboxView[]).map(name =>
        <Link href={inbox + "?view=" + name} key={name} aria-current={name === view ? "page" : undefined}>{views[name].label}</Link>)}
    </nav>
    {view === "all" && <nav className="issue-nav issue-filters" aria-label="Status filter">
      <Link href={inbox + "?view=all"} aria-current={!done ? "true" : undefined}>All statuses</Link>
      <Link href={inbox + "?view=all&status=done"} aria-current={done ? "true" : undefined}>Done</Link>
    </nav>}
    <div className={"inbox-grid" + (issueId ? " has-issue" : "")}>
      <section className="team-section inbox-list" aria-labelledby="inbox-heading">
        <h2 id="inbox-heading">{listName}{list ? " · " + list.total : ""}</h2>
        {listError ? <p className="error" role="alert">{listError}</p> :
          !list ? <p role="status" className="hint">Loading issues…</p> :
            list.items.length ? <ul className="team-rows">
              {list.items.map(issue => issue.source && <li key={issue.issueId}>
                <Link href={inbox + "?" + query + "&issue=" + issue.issueId}
                  aria-current={issueId === issue.issueId ? "true" : undefined} className="inbox-item">
                  <span className="nav-icon" aria-hidden="true">○</span>
                  <span><strong>{issue.source.title}</strong><small>{issueIdentifier(issue.source)} · {sourceState(issue.source.state)}</small></span>
                  {view !== "needsReview" && <span className="inbox-status">{statusLabel(issue.triage.status)}</span>}
                </Link>
              </li>)}
            </ul> : <p className="hint">{views[view].empty}</p>}
        {list && list.total > list.pageSize && <nav className="pages" aria-label="Inbox pages">
          {page > 1 && <Link href={inbox + "?view=" + view + filter + "&page=" + (page - 1)}>Previous</Link>}
          <span>Page {list.page}</span>
          {page * list.pageSize < list.total && <Link href={inbox + "?view=" + view + filter + "&page=" + (page + 1)}>Next</Link>}
        </nav>}
      </section>
      {issueId && <section className="team-section issue-detail" aria-label="Issue detail">
        <Link href={inbox + "?" + query}>Back to {listName.toLowerCase()}</Link>
        {detailError?.id === issueId ? <p className="error" role="alert">{detailError.message}</p> :
          !selected ? <p role="status" className="hint">Loading issue…</p> :
            <IssueWorkspaceDetail key={selected.issueId} issue={selected} workspace={workspace} account={account}
              myWorkHref={inbox + "?view=myWork"} onChanged={() => setRevision(value => value + 1)} />}
      </section>}
    </div>
  </>;
}
