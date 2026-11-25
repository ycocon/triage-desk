"use client";

import Link from "next/link";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { ApiError, api, messageFor, type Schema } from "@/lib/http";

const date = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
const labels = {
  needsReview: "Needs review", ready: "Ready", waitingForInformation: "Waiting for information",
  inProgress: "In progress", done: "Done", setAside: "Set aside",
} satisfies Record<Schema["Triage"]["status"], string>;

export function statusLabel(status: Schema["Triage"]["status"]) { return labels[status]; }
export function sourceState(state: Schema["SourceSummary"]["state"]) {
  return state === "open" ? "GitHub Open" : "GitHub Closed";
}
export function issueIdentifier(source: Schema["SourceSummary"]) {
  const repository = new URL(source.url).pathname.split("/").slice(1, 3).join("/");
  return repository + " #" + source.number;
}

const activityLabels: Record<string, string> = {
  accepted: "accepted the issue",
  claimed: "claimed the issue",
  started: "started work",
  completed: "marked the issue Done",
  assignmentReturned: "returned the issue to review",
};

type Draft =
  | { kind: "accept"; priority: Schema["Accept"]["priority"]; nextStep: string }
  | { kind: "claim" | "start" | "complete" };

export function IssueWorkspaceDetail({ issue, workspace, account, onChanged, myWorkHref }: {
  issue: Schema["IssueDetail"]; workspace: Schema["Workspace"]; account: Schema["User"];
  onChanged: () => void; myWorkHref: string;
}) {
  const path = "/workspaces/" + workspace.workspaceId + "/issues/" + issue.issueId;
  const [localTriage, setTriage] = useState<Schema["Triage"]>();
  const triage = localTriage && localTriage.decisionVersion >= issue.triage.decisionVersion
    ? localTriage : issue.triage;
  const [history, setHistory] = useState<Schema["ActivityPage"]>();
  const [historyError, setHistoryError] = useState("");
  const [historyPage, setHistoryPage] = useState(1);
  const [revision, setRevision] = useState(0);
  const [priority, setPriority] = useState<Schema["Accept"]["priority"]>("normal");
  const [nextStep, setNextStep] = useState("");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");
  const retry = useRef<{ key: string; id: string }>(null);

  useEffect(() => {
    const controller = new AbortController();
    api<Schema["ActivityPage"]>(path + "/activity?page=" + historyPage, { signal: controller.signal })
      .then(result => {
        if (controller.signal.aborted) return;
        setHistory(result);
        setHistoryError("");
      })
      .catch(failure => { if (!controller.signal.aborted) setHistoryError(messageFor(failure)); });
    return () => controller.abort();
  }, [path, revision, historyPage]);

  async function act(draft: Draft) {
    if (busy) return;
    setBusy(true);
    setError("");
    setNotice("");
    const expectedVersion = triage.decisionVersion;
    const key = JSON.stringify({ draft, expectedVersion });
    const commandId = retry.current?.key === key ? retry.current.id : crypto.randomUUID();
    retry.current = { key, id: commandId };
    const body = draft.kind === "claim" ? { commandId, expectedVersion } : { ...draft, commandId, expectedVersion };

    try {
      const result = await api<Schema["Triage"]>(path + (draft.kind === "claim" ? "/claim" : "/commands"), {
        method: "POST", body: JSON.stringify(body),
      });
      retry.current = null;
      setTriage(result);
      setNotice({ accept: "Issue accepted.", claim: "Issue claimed.", start: "Work started.", complete: "Issue marked Done." }[draft.kind]);
      reloadHistory();
    } catch (failure) {
      if (failure instanceof ApiError && failure.status < 500) retry.current = null;
      if (failure instanceof ApiError && failure.currentTriage) {
        setTriage(failure.currentTriage);
        reloadHistory();
      }
      setError(messageFor(failure));
    } finally {
      setBusy(false);
    }
  }

  function reloadHistory() {
    setHistoryPage(1);
    setRevision(value => value + 1);
    onChanged();
  }

  function accept(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!nextStep.trim()) {
      setError("Enter the next step before accepting.");
      return;
    }
    void act({ kind: "accept", priority, nextStep: nextStep.trim() });
  }

  const myAssignment = triage.assigneeId === account.userId;
  const activityAssignee = history?.items.find(item => item.actorId === triage.assigneeId);
  const assignee = myAssignment ? account.displayName : activityAssignee?.actorName;
  const source = issue.source;
  const report = source && <>
    {source.labels.length > 0 && <ul className="issue-labels" aria-label="GitHub labels">
      {source.labels.map((label, index) => <li key={label + "-" + index}>{label}</li>)}
    </ul>}
    {source.body && <p className="issue-body">{source.body}</p>}
    <a href={source.url} target="_blank" rel="noreferrer">View on GitHub</a>
  </>;

  return <>
    {source ? <>
      <p className="issue-number">{issueIdentifier(source)}</p>
      <h2>{source.title}</h2>
    </> : <h2>Source unavailable</h2>}
    <div className="issue-states">
      <span>{statusLabel(triage.status)}</span>
      {source && <span>{sourceState(source.state)}</span>}
      {triage.priority && <span className="role">{triage.priority} priority</span>}
    </div>
    {triage.assigneeId && <p className="hint">Assigned to <strong>{assignee ?? "a workspace teammate"}</strong></p>}
    {triage.nextStep && <div className="next-step"><h3>Next step</h3><p>{triage.nextStep}</p></div>}
    {triage.status === "needsReview" && report}

    {source && <div className="issue-actions" aria-busy={busy}>
      {triage.status === "needsReview" && workspace.myRole !== "member" && <form onSubmit={accept} className="decision-form">
        <label>Priority <select value={priority} disabled={busy} onChange={event => setPriority(event.target.value as typeof priority)}>
          <option value="high">High</option><option value="normal">Normal</option><option value="low">Low</option>
        </select></label>
        <label>Next step<textarea rows={3} required maxLength={2000} disabled={busy}
          value={nextStep} onChange={event => setNextStep(event.target.value)} /></label>
        <button className="primary" disabled={busy}>Accept issue</button>
      </form>}
      {triage.status === "ready" && !triage.assigneeId &&
        <button className="primary" disabled={busy} onClick={() => void act({ kind: "claim" })}>Claim issue</button>}
      {triage.status === "ready" && myAssignment &&
        <button className="primary" disabled={busy} onClick={() => void act({ kind: "start" })}>Start work</button>}
      {triage.status === "inProgress" && myAssignment &&
        <button className="primary" disabled={busy} onClick={() => void act({ kind: "complete" })}>Mark Done</button>}
      {notice === "Issue claimed." && <Link href={myWorkHref}>Open My work</Link>}
    </div>}
    {notice && <p className="notice" role="status">{notice}</p>}
    {error && <p className="error" role="alert">{error}</p>}
    {triage.status !== "needsReview" && report}

    <section className="issue-history" aria-label="Issue history">
      <h3>History</h3>
      {historyError ? <p className="error" role="alert">{historyError}</p> :
        !history ? <p className="hint" role="status">Loading history…</p> :
          history.items.length === 0 ? <p className="hint">No workspace decisions yet.</p> :
            <ol className="team-rows">
              {history.items.map(item => <li key={item.activityId}>
                <p><strong>{item.actorName ?? "Triage Desk"}</strong> {activityLabels[item.kind] ?? item.kind}</p>
                <time dateTime={item.occurredAt}>{date.format(new Date(item.occurredAt))}</time>
              </li>)}
            </ol>}
      {history && history.total > history.pageSize && <nav className="pages" aria-label="History pages">
        <button disabled={historyPage <= 1} onClick={() => setHistoryPage(historyPage - 1)}>Previous</button>
        <span>Page {history.page}</span>
        <button disabled={historyPage * history.pageSize >= history.total} onClick={() => setHistoryPage(historyPage + 1)}>Next</button>
      </nav>}
    </section>
  </>;
}
