"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { api, messageFor, type Schema } from "@/lib/http";
import { inboxHref } from "./inbox-query";

const stamp = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

export function WorkspaceOverview({ workspace }: { workspace: Schema["Workspace"] }) {
  const path = "/workspaces/" + workspace.workspaceId;
  const [overview, setOverview] = useState<Schema["Overview"]>();
  const [workload, setWorkload] = useState<Schema["Workload"]>();
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    const options = { signal: controller.signal };
    Promise.allSettled([api<Schema["Overview"]>(path + "/reports/overview", options),
      api<Schema["Workload"]>(path + "/reports/workload", options)]).then(([queue, work]) => {
        if (controller.signal.aborted) return;
        if (queue.status === "fulfilled") setOverview(queue.value);
        if (work.status === "fulfilled") setWorkload(work.value);
        const failure = queue.status === "rejected" ? queue : work.status === "rejected" ? work : undefined;
        setError(failure ? messageFor(failure.reason) : "");
      });
    return () => controller.abort();
  }, [path]);

  const review = { view: "needsReview", page: 1 } as const;
  const ready = { view: "available", page: 1 } as const;
  return <>
    <div className="workspace-heading"><span className="workspace-initial" aria-hidden="true">{workspace.name.slice(0, 1)}</span>
      <div><h1>{workspace.name}</h1><p className="hint">Private workspace <span aria-hidden="true">·</span> <span className="role">{workspace.myRole}</span></p></div>
    </div>
    <div className="workspace-links overview-links">
      <Link href={inboxHref(path, review)}>Open triage inbox</Link>
      <Link href={path + "/repositories"}>View repositories</Link>
      <Link href={path + "/team"}>View team</Link>
      <Link href="/workspaces">View workspaces</Link>
    </div>
    {!overview && !workload && !error && <p role="status" className="hint">Loading workspace reports…</p>}
    {error && <p role="alert" className="error">{error}</p>}
    {overview && <section className="team-section" aria-labelledby="queue-heading">
      <h2 id="queue-heading">Issue queues</h2>
      <div className="queue-metrics">
        <Link href={inboxHref(path, review)}><strong>{overview.needsReview}</strong>Needs review</Link>
        <Link href={inboxHref(path, ready)}><strong>{overview.availableToClaim}</strong>Available to claim</Link>
        <div><span>Oldest in review</span>
          {overview.oldestIssueId && overview.oldestEnteredReviewAt ?
            <Link href={inboxHref(path, { ...review, issue: overview.oldestIssueId })}>
              <time dateTime={overview.oldestEnteredReviewAt}>{stamp.format(new Date(overview.oldestEnteredReviewAt))}</time>
            </Link> : <small>No issues waiting for review</small>}
        </div>
      </div>
    </section>}
    {workload && <section className="team-section" aria-labelledby="workload-heading">
      <h2 id="workload-heading">Active workload</h2>
      <ul className="team-rows report-rows">
        {workload.byAssignee.map(member => <li key={member.userId}>
          <Link href={inboxHref(path, { view: "activeWork", page: 1, assigneeId: member.userId })}>
            <span>{member.displayName}</span><strong>{member.activeIssues} active</strong>
          </Link>
        </li>)}
        <li><Link href={inboxHref(path, ready)}><span>Unassigned</span><strong>{workload.unassignedReady} ready</strong></Link></li>
      </ul>
    </section>}
  </>;
}
