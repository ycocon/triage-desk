"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { api, messageFor, type Schema } from "@/lib/http";
import { IssueWorkspaceDetail, issueIdentifier, sourceState, statusLabel } from "./issue-workspace-detail";
import { InboxFilters } from "./inbox-filters";
import { inboxHref, inboxParams, inboxViews, type InboxQuery } from "./inbox-query";

const views = {
  needsReview: { label: "Needs review", empty: "No issues need review. Refresh a connected repository to import open issues." },
  available: { label: "Available to claim", empty: "No issues available to claim." },
  myWork: { label: "My work", empty: "No active assignments." },
  all: { label: "All issues", empty: "No issues match this view." },
  activeWork: { label: "Active work", empty: "No active issues match these filters." },
  sourceChanges: { label: "Source changes", empty: "No GitHub source changes need review." },
};

export function InboxPanel({ workspace, account, filters, onIssuesChanged }: {
  workspace: Schema["Workspace"]; account: Schema["User"]; filters: InboxQuery; onIssuesChanged: () => void;
}) {
  const router = useRouter();
  const path = "/workspaces/" + workspace.workspaceId;
  const { issue: issueId, page, view, status } = filters;
  const query = inboxParams(filters);
  const filterKey = inboxParams({ ...filters, page: 1 });
  const back = inboxHref(path, { ...filters, issue: undefined });
  const [result, setResult] = useState<{ query: string; revision: number; page: Schema["IssueSummaryPage"] }>();
  const [detail, setDetail] = useState<{ key: string; issue: Schema["IssueDetail"] }>();
  const [listError, setListError] = useState<{ query: string; revision: number; message: string }>();
  const [detailError, setDetailError] = useState<{ id: string; message: string }>();
  const [revision, setRevision] = useState(0);
  const detailKey = query + "&issue=" + issueId;
  const listHeading = useRef<HTMLHeadingElement>(null);
  const detailStart = useRef<HTMLAnchorElement>(null);
  const previousIssue = useRef<string | undefined>(undefined);
  const lastList = useRef<string | undefined>(undefined);

  useEffect(() => {
    const controller = new AbortController();
    api<Schema["IssueSummaryPage"]>(path + "/issues?" + query, { signal: controller.signal })
      .then(items => {
        if (controller.signal.aborted) return;
        setResult({ query, revision, page: items });
        setListError(undefined);
      }).catch(failure => {
        if (!controller.signal.aborted) setListError({ query, revision, message: messageFor(failure) });
      });
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
  const list = result?.query === query && result.revision === revision ? result.page : undefined;
  const listFailure = listError?.query === query && listError.revision === revision ? listError.message : undefined;
  const listName = status ? statusLabel(status) : views[view].label;
  const listReady = Boolean(list || listFailure);
  useEffect(() => {
    if (issueId && previousIssue.current !== issueId) {
      previousIssue.current = issueId;
      detailStart.current?.focus();
      return;
    }
    if (issueId || !listReady) return;
    if (previousIssue.current) {
      const row = lastList.current === query ? document.getElementById("inbox-issue-" + previousIssue.current) : null;
      (row ?? listHeading.current)?.focus();
      previousIssue.current = undefined;
    } else if (lastList.current && lastList.current !== query) {
      listHeading.current?.focus();
    }
    lastList.current = query;
  }, [issueId, listReady, query]);

  return <>
    <div className="list-heading"><div><h1>Triage inbox</h1><p className="hint">Review, claim, and track workspace issues.</p></div>
      <Link href={path + "/repositories"}>View repositories</Link></div>
    <nav className="issue-nav" aria-label="Issue views">
      {inboxViews.map(name =>
        <Link href={inboxHref(path, { view: name, page: 1 })} key={name}
          aria-current={name === view ? "page" : undefined}>{views[name].label}</Link>)}
    </nav>
    {view === "all" && <nav className="issue-nav issue-filters" aria-label="Status filter">
      <Link href={inboxHref(path, { ...filters, page: 1, issue: undefined, status: undefined })} aria-current={!status ? "true" : undefined}>All statuses</Link>
      <Link href={inboxHref(path, { ...filters, page: 1, issue: undefined, status: "done" })} aria-current={status === "done" ? "true" : undefined}>Done</Link>
      <Link href={inboxHref(path, { ...filters, page: 1, issue: undefined, status: "setAside" })} aria-current={status === "setAside" ? "true" : undefined}>Set aside</Link>
    </nav>}
    <div className={issueId ? "inbox-with-selection" : undefined}>
      <InboxFilters key={filterKey} path={path} query={filters} onApply={next => router.push(inboxHref(path, next), { scroll: false })} />
    </div>
    <div className={"inbox-grid" + (issueId ? " has-issue" : "")}>
      <section className="team-section inbox-list" aria-labelledby="inbox-heading">
        <h2 id="inbox-heading" tabIndex={-1} ref={listHeading}>{listName}{list ? " · " + list.total : ""}</h2>
        {listFailure ? <p className="error" role="alert">{listFailure}</p> :
          !list ? <p role="status" className="hint">Loading issues…</p> :
            list.items.length ? <ul className="team-rows">
              {list.items.map(issue => issue.source && <li key={issue.issueId}>
                <Link href={inboxHref(path, { ...filters, issue: issue.issueId })} scroll={false}
                  id={"inbox-issue-" + issue.issueId} tabIndex={0}
                  aria-current={issueId === issue.issueId ? "true" : undefined} className="inbox-item">
                  <span className="nav-icon" aria-hidden="true">○</span>
                  <span><strong>{issue.source.title}</strong><small>{issueIdentifier(issue.source)} · {sourceState(issue.source.state)}</small>
                    {view === "myWork" && issue.source.state === "closed" && <small className="closure-label">Assigned to you · Closed on GitHub</small>}
                  </span>
                  {view !== "needsReview" && <span className="inbox-status">{statusLabel(issue.triage.status)}</span>}
                </Link>
              </li>)}
            </ul> : <p className="hint">{filterKey !== inboxParams({ view, page: 1 }) ? "No issues match these filters." : views[view].empty}</p>}
        {list && (page > 1 || list.total > list.pageSize) && <nav className="pages" aria-label="Inbox pages">
          {page > 1 && <Link href={inboxHref(path, { ...filters, page: page - 1, issue: undefined })}>Previous</Link>}
          <span>Page {list.page}</span>
          {page * list.pageSize < list.total && <Link href={inboxHref(path, { ...filters, page: page + 1, issue: undefined })}>Next</Link>}
        </nav>}
      </section>
      {issueId && <section className="team-section issue-detail" aria-label="Issue detail">
        <Link href={back} scroll={false} ref={detailStart}>Back to {listName.toLowerCase()}</Link>
        {detailError?.id === issueId ? <p className="error" role="alert">{detailError.message}</p> :
          !selected ? <p role="status" className="hint">Loading issue…</p> :
            <IssueWorkspaceDetail key={selected.issueId} issue={selected} workspace={workspace} account={account}
              myWorkHref={inboxHref(path, { view: "myWork", page: 1 })} onChanged={() => {
                setRevision(value => value + 1);
                onIssuesChanged();
              }} />}
      </section>}
    </div>
  </>;
}
