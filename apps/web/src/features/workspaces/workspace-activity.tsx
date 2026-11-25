"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { api, messageFor, type Schema } from "@/lib/http";
import { inboxHref } from "./inbox-query";
import { activityLabel } from "./issue-workspace-detail";
import { connectedRepositories } from "./workspace-options";

const stamp = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
export type ActivityQuery = { page: number; repositoryId?: string };

function RepositoryFilter({ repositoryId, repositories, apply }: {
  repositoryId?: string; repositories: Schema["Repository"][]; apply: (id: string) => void;
}) {
  const [selected, setSelected] = useState(repositoryId ?? "");
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    apply(selected);
  }
  return <form className="activity-filter" onSubmit={submit}>
    <label>Repository<select value={selected} onChange={event => setSelected(event.target.value)}>
      <option value="">All repositories</option>
      {repositoryId && !repositories.some(repo => repo.repositoryId === repositoryId) &&
        <option value={repositoryId}>Selected repository</option>}
      {repositories.map(repo => <option key={repo.repositoryId} value={repo.repositoryId}>{repo.fullName}</option>)}
    </select></label>
    <button>Apply filter</button>
  </form>;
}

export function WorkspaceActivity({ path, filters }: { path: string; filters: ActivityQuery }) {
  const router = useRouter();
  const { page, repositoryId } = filters;
  const query = "page=" + page + (repositoryId ? "&repositoryId=" + repositoryId : "");
  const href = (value: number) => path + "/activity?page=" + value + (repositoryId ? "&repositoryId=" + repositoryId : "");
  const [result, setResult] = useState<{ query: string; page: Schema["ActivityPage"] }>();
  const [repositories, setRepositories] = useState<Schema["Repository"][]>([]);
  const [error, setError] = useState<{ query: string; message: string }>();
  const heading = useRef<HTMLHeadingElement>(null);
  const lastPage = useRef<string | undefined>(undefined);

  useEffect(() => {
    const controller = new AbortController();
    api<Schema["ActivityPage"]>(path + "/activity?" + query, { signal: controller.signal }).then(items => {
      if (controller.signal.aborted) return;
      setResult({ query, page: items });
      setError(undefined);
    }).catch(failure => {
      if (!controller.signal.aborted) setError({ query, message: messageFor(failure) });
    });
    return () => controller.abort();
  }, [path, query]);

  useEffect(() => {
    const controller = new AbortController();
    connectedRepositories(path, controller.signal).then(items => {
      if (!controller.signal.aborted) setRepositories(items);
    }).catch(() => { /* The activity remains readable with the current filter. */ });
    return () => controller.abort();
  }, [path]);

  const list = result?.query === query ? result.page : undefined;
  useEffect(() => {
    if (!list) return;
    if (lastPage.current && lastPage.current !== query) heading.current?.focus();
    lastPage.current = query;
  }, [list, query]);

  return <>
    <h1 ref={heading} tabIndex={-1}>Activity</h1>
    <RepositoryFilter key={repositoryId ?? ""} repositoryId={repositoryId} repositories={repositories}
      apply={selected => router.push(path + "/activity" + (selected ? "?repositoryId=" + selected : ""))} />
    <section className="team-section" aria-labelledby="activity-heading">
      <h2 id="activity-heading">Issue activity{list ? " · " + list.total : ""}</h2>
      {error?.query === query ? <p role="alert" className="error">{error.message}</p> :
        !list ? <p role="status" className="hint">Loading activity…</p> :
          list.items.length ? <ul className="team-rows">
            {list.items.map(entry => <li key={entry.activityId} className="team-row">
              <div className="team-person">
                <span><strong>{entry.actorName ?? (entry.actorKind === "system" ? "Triage Desk" : "Workspace member")}</strong>
                  {" "}{activityLabel(entry.kind)}</span>
                <small><time dateTime={entry.occurredAt}>{stamp.format(new Date(entry.occurredAt))}</time></small>
              </div>
              <Link className="activity-issue" href={inboxHref(path, { view: "all", page: 1, issue: entry.issueId })}>Open issue</Link>
            </li>)}
          </ul> : <p className="hint">No issue activity matches this view.</p>}
      {list && (page > 1 || list.total > list.pageSize) && <nav className="pages" aria-label="Activity pages">
        {page > 1 && <Link href={href(page - 1)}>Previous</Link>}
        <span>Page {page}</span>
        {page * list.pageSize < list.total && <Link href={href(page + 1)}>Next</Link>}
      </nav>}
    </section>
  </>;
}
