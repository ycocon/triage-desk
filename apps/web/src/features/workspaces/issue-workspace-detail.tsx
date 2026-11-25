"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { ApiError, api, messageFor, type Schema } from "@/lib/http";
import { IssueDecisionEditor, type DecisionDraft, type EditorKind } from "./issue-decision-editor";
import { activeMembers } from "./workspace-options";

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
  informationRequested: "requested information",
  questionAnswered: "answered the question",
  setAside: "set the issue aside",
  returnedToReview: "returned the issue to review",
  reassigned: "changed the assignment",
  priorityChanged: "changed the priority",
  severityChanged: "changed the bug severity",
  nextStepEdited: "edited the next step",
  sourceChangeAcknowledged: "acknowledged the source change",
  sourceStateChanged: "flagged a GitHub state change",
  sourceTransferred: "verified a GitHub transfer",
  sourceTransferConflict: "flagged a conflicting GitHub transfer",
};

export function activityLabel(kind: string) { return activityLabels[kind] ?? "updated the issue"; }

const sourceChangeText = {
  closedWhileActive: "GitHub closed this issue while work was active.",
  reopenedAfterDecision: "GitHub reopened this issue after a workspace decision.",
  transferConflict: "Two workspace records match this GitHub transfer. Review their histories before resolving it.",
} satisfies Record<NonNullable<Schema["Triage"]["sourceReviewFlag"]>, string>;

const actionLabels: Record<EditorKind, string> = {
  accept: "Accept issue", requestInformation: "Request information", answer: "Answer question",
  setAside: "Set aside", returnToReview: "Return to review", reassign: "Reassign",
  changePriority: "Change priority", changeSeverity: "Change severity",
  editNextStep: "Edit next step", acknowledgeSourceChange: "Review source change",
};

const decisionMessages = {
  accept: "Issue accepted.", claim: "Issue claimed.", start: "Work started.", complete: "Issue marked Done.",
  requestInformation: "Question sent.", answer: "Answer saved. Issue returned to review.",
  setAside: "Issue set aside.", returnToReview: "Issue returned to review.", reassign: "Assignment saved.",
  changePriority: "Priority saved.", changeSeverity: "Severity saved.", editNextStep: "Next step saved.",
  acknowledgeSourceChange: "Source change acknowledged.",
} satisfies Record<DecisionDraft["kind"], string>;

function permittedEditors(triage: Schema["Triage"], reviewer: boolean, assigned: boolean, bug: boolean) {
  const kinds: EditorKind[] = [];
  if (triage.status === "needsReview" && reviewer) kinds.push("accept", "requestInformation", "setAside");
  if (triage.status === "waitingForInformation" && assigned && triage.question) kinds.push("answer");
  if (triage.status === "ready" || triage.status === "inProgress") {
    if (reviewer || assigned) kinds.push("editNextStep");
    if (reviewer) kinds.push("reassign", "changePriority");
  }
  if (triage.status === "waitingForInformation" && reviewer) kinds.push("reassign");
  if ((triage.status === "done" || triage.status === "setAside") && reviewer) kinds.push("returnToReview");
  if ((bug || triage.severity) && reviewer) kinds.push("changeSeverity");
  if (reviewer && (triage.sourceReviewFlag === "closedWhileActive" || triage.sourceReviewFlag === "reopenedAfterDecision"))
    kinds.push("acknowledgeSourceChange");
  return kinds;
}

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
  const [editor, setEditor] = useState<EditorKind>();
  const [members, setMembers] = useState<Schema["Member"][]>();
  const [memberError, setMemberError] = useState("");
  const [stale, setStale] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");
  const retry = useRef<{ key: string; id: string }>(null);
  const editorForm = useRef<HTMLFormElement>(null);
  const focusEditor = useRef(false);
  const decisionNotice = useRef<HTMLParagraphElement>(null);
  const returnToAction = useRef<EditorKind | undefined>(undefined);
  const editorActions = useRef<Partial<Record<EditorKind, HTMLButtonElement | null>>>({});
  const reviewer = workspace.myRole !== "member";
  const needsMembers = reviewer || Boolean(triage.assigneeId && triage.assigneeId !== account.userId);

  useEffect(() => {
    if (!needsMembers) return;
    const controller = new AbortController();
    activeMembers("/workspaces/" + workspace.workspaceId, controller.signal).then(result => {
      if (controller.signal.aborted) return;
      setMembers(result);
      setMemberError("");
    }).catch(failure => { if (!controller.signal.aborted) setMemberError(messageFor(failure)); });
    return () => controller.abort();
  }, [workspace.workspaceId, needsMembers, revision]);

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

  async function act(draft: DecisionDraft) {
    if (busy) return;
    setBusy(true);
    setError("");
    setNotice("");
    setStale(false);
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
      setEditor(undefined);
      setNotice(decisionMessages[draft.kind]);
      reloadHistory();
    } catch (failure) {
      if (failure instanceof ApiError && failure.status < 500) retry.current = null;
      if (failure instanceof ApiError && failure.currentTriage) {
        setTriage(failure.currentTriage);
        setStale(failure.code === "stale_decision");
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

  const myAssignment = triage.assigneeId === account.userId;
  const activityAssignee = history?.items.find(item => item.actorId === triage.assigneeId);
  const assignee = myAssignment ? account.displayName : members?.find(member => member.userId === triage.assigneeId)?.displayName
    ?? activityAssignee?.actorName;
  const source = issue.source;
  const bug = source?.labels.some(label => label.toLowerCase() === "bug") ?? false;
  const available = permittedEditors(triage, reviewer, myAssignment, bug);
  const defaultEditor = available.includes("accept") ? "accept" : available.includes("answer") ? "answer" : undefined;
  const currentEditor = editor ?? defaultEditor;
  useEffect(() => {
    if (focusEditor.current && editorForm.current) {
      editorForm.current.querySelector<HTMLElement>("select:not([disabled]), textarea:not([disabled]), button:not([disabled])")?.focus();
      focusEditor.current = false;
    }
    if (returnToAction.current) {
      editorActions.current[returnToAction.current]?.focus();
      returnToAction.current = undefined;
    }
  }, [currentEditor]);

  useEffect(() => {
    if (notice) decisionNotice.current?.focus();
  }, [notice]);

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
      {triage.severity && <span className="role">{triage.severity} severity</span>}
    </div>
    {triage.assigneeId && <p className="hint">Assigned to <strong>{assignee ?? "a workspace teammate"}</strong></p>}
    {triage.nextStep && <div className="next-step"><h3>Next step</h3><p>{triage.nextStep}</p></div>}
    {triage.question && <div className="next-step"><h3>Question</h3><p>{triage.question}</p></div>}
    {triage.deferralReason && <div className="next-step"><h3>Reason set aside</h3><p>{triage.deferralReason}</p></div>}
    {triage.sourceReviewFlag && <p className="hint">{sourceChangeText[triage.sourceReviewFlag]}</p>}
    {triage.status === "needsReview" && report}

    {source && <div className="issue-actions" aria-busy={busy}>
      {triage.status === "ready" && !triage.assigneeId &&
        <button className="primary" disabled={busy} onClick={() => void act({ kind: "claim" })}>Claim issue</button>}
      {triage.status === "ready" && myAssignment &&
        <button className="primary" disabled={busy} onClick={() => void act({ kind: "start" })}>Start work</button>}
      {triage.status === "inProgress" && myAssignment &&
        <button className="primary" disabled={busy} onClick={() => void act({ kind: "complete" })}>Mark Done</button>}
      {notice === "Issue claimed." && <Link href={myWorkHref}>Open My work</Link>}
      {available.filter(kind => kind !== currentEditor).map(kind =>
        <button key={kind} ref={button => { editorActions.current[kind] = button; }} disabled={busy}
          onClick={() => { focusEditor.current = true; setEditor(kind); setError(""); setStale(false); }}>
          {actionLabels[kind]}
        </button>)}
      {currentEditor && <IssueDecisionEditor key={currentEditor} kind={currentEditor} triage={triage}
        members={members} bug={bug} busy={busy} allowed={available.includes(currentEditor)} formRef={editorForm}
        cancel={editor && editor !== defaultEditor ? () => {
          returnToAction.current = editor;
          setEditor(undefined);
          setError("");
          setStale(false);
        } : undefined}
        submit={draft => { setEditor(currentEditor); void act(draft); }} />}
    </div>}
    {notice && <p className="notice" role="status" tabIndex={-1} ref={decisionNotice}>{notice}</p>}
    {error && <p className="error" role="alert">{error}
      {stale && " The latest decision is shown above. Review it alongside your draft before saving again."}</p>}
    {memberError && <p className="hint">Could not load teammate options. {memberError}</p>}
    {triage.status !== "needsReview" && report}

    <section className="issue-history" aria-label="Issue history">
      <h3>History</h3>
      {historyError ? <p className="error" role="alert">{historyError}</p> :
        !history ? <p className="hint" role="status">Loading history…</p> :
          history.items.length === 0 ? <p className="hint">No workspace decisions yet.</p> :
            <ol className="team-rows">
              {history.items.map(item => <li key={item.activityId}>
                <p><strong>{item.actorName ?? "Triage Desk"}</strong> {activityLabel(item.kind)}</p>
                <time dateTime={item.occurredAt}>{date.format(new Date(item.occurredAt))}</time>
                {item.reason && <p className="history-note">{item.reason}</p>}
                {item.changes.filter(change => change.field === "question" || change.field === "answer")
                  .map(change => <p className="history-note" key={change.field}>
                    <strong>{change.field === "question" ? "Question" : "Answer"}</strong>{" "}
                    {String(change.after ?? change.before ?? "")}
                  </p>)}
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
