"use client";

import Link from "next/link";
import { useEffect, useState, type FormEvent } from "react";
import { messageFor, type Schema } from "@/lib/http";
import { inboxHref, priorities, readInboxQuery, sorts, statuses, type InboxQuery } from "./inbox-query";
import { activeMembers, connectedRepositories } from "./workspace-options";
import { statusLabel } from "./issue-workspace-detail";

const sortLabels = {
  enteredReviewAt: "Inbox arrival", priority: "Priority", updatedAt: "Last triage update", number: "Issue number",
};

export function InboxFilters({ path, query, onApply }: {
  path: string; query: InboxQuery; onApply: (query: InboxQuery) => void;
}) {
  const [repositories, setRepositories] = useState<Schema["Repository"][]>([]);
  const [members, setMembers] = useState<Schema["Member"][]>([]);
  const [error, setError] = useState("");
  const [repositoryValue, setRepositoryValue] = useState(query.repositoryId ?? "");
  const [assigneeValue, setAssigneeValue] = useState(query.assigneeId ?? "");

  useEffect(() => {
    const controller = new AbortController();
    Promise.allSettled([connectedRepositories(path, controller.signal), activeMembers(path, controller.signal)])
      .then(([repos, people]) => {
        if (controller.signal.aborted) return;
        if (repos.status === "fulfilled") setRepositories(repos.value);
        if (people.status === "fulfilled") setMembers(people.value);
        const failure = repos.status === "rejected" ? repos : people.status === "rejected" ? people : undefined;
        setError(failure ? messageFor(failure.reason) : "");
      });
    return () => controller.abort();
  }, [path]);

  function apply(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const values = Object.fromEntries(Array.from(new FormData(event.currentTarget),
      ([name, value]) => [name, String(value)]));
    onApply(readInboxQuery({ ...values, view: query.view }));
  }

  return <form className="inbox-filter-form" onSubmit={apply} aria-label="Issue filters">
    <div className="inbox-search">
      <label>Search issues<input type="search" name="q" defaultValue={query.q} maxLength={160}
        placeholder="Title or owner/repo #42" /></label>
      <button className="primary">Apply filters</button>
      <Link href={inboxHref(path, { view: query.view, page: 1 })}>Clear filters</Link>
    </div>
    <details open={Boolean(query.repositoryId || query.status || query.githubState || query.priority ||
        query.assigneeId || query.sort || query.direction) || undefined}>
      <summary>Filter and sort options</summary>
      <div className="inbox-filter-controls">
        <label>Repository<select name="repositoryId" value={repositoryValue}
          onChange={event => setRepositoryValue(event.target.value)}>
          <option value="">Any repository</option>
          {query.repositoryId && !repositories.some(repo => repo.repositoryId === query.repositoryId) &&
            <option value={query.repositoryId}>Selected repository</option>}
          {repositories.map(repo => <option key={repo.repositoryId} value={repo.repositoryId}>{repo.fullName}</option>)}
        </select></label>
        <label>Workspace status<select name="status" defaultValue={query.status ?? ""}>
          <option value="">Any status</option>
          {statuses.map(status => <option key={status} value={status}>{statusLabel(status)}</option>)}
        </select></label>
        <label>GitHub state<select name="githubState" defaultValue={query.githubState ?? ""}>
          <option value="">Any state</option><option value="open">Open</option><option value="closed">Closed</option>
        </select></label>
        <label>Priority<select name="priority" defaultValue={query.priority ?? ""}>
          <option value="">Any priority</option>
          {priorities.map(priority => <option key={priority} value={priority}>{priority[0].toUpperCase() + priority.slice(1)}</option>)}
        </select></label>
        <label>Assignee<select name="assigneeId" value={assigneeValue}
          onChange={event => setAssigneeValue(event.target.value)}>
          <option value="">Anyone</option><option value="unassigned">Unassigned</option>
          {query.assigneeId && query.assigneeId !== "unassigned" && !members.some(member => member.userId === query.assigneeId) &&
            <option value={query.assigneeId}>Selected assignee</option>}
          {members.map(member => <option key={member.userId} value={member.userId}>{member.displayName}</option>)}
        </select></label>
        <label>Sort by<select name="sort" defaultValue={query.sort ?? ""}>
          <option value="">Queue default</option>
          {sorts.map(sort => <option key={sort} value={sort}>{sortLabels[sort]}</option>)}
        </select></label>
        <label>Sort direction<select name="direction" defaultValue={query.direction ?? ""}>
          <option value="">Default</option><option value="asc">Ascending</option><option value="desc">Descending</option>
        </select></label>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
    </details>
  </form>;
}
