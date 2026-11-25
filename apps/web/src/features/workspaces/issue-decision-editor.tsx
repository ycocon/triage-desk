"use client";

import { useId, type FormEvent, type Ref } from "react";
import type { Schema } from "@/lib/http";

type WithoutCommandIdentity<T> = T extends unknown ? Omit<T, "commandId" | "expectedVersion"> : never;
export type DecisionDraft = WithoutCommandIdentity<Schema["TriageCommand"]> | { kind: "claim" };
export type EditorKind = Exclude<DecisionDraft["kind"], "claim" | "start" | "complete">;

const fields = {
  accept: { priority: true, assignee: "optional", note: "Next step", submit: "Accept issue" },
  requestInformation: { assignee: "required", note: "Question", submit: "Send question" },
  answer: { note: "Answer", submit: "Submit answer" },
  setAside: { note: "Reason", submit: "Set issue aside" },
  returnToReview: { note: "Reason (optional)", optionalNote: true, submit: "Return issue to review" },
  reassign: { assignee: "status", submit: "Save assignee" },
  changePriority: { priority: true, submit: "Save priority" },
  changeSeverity: { severity: true, submit: "Save severity" },
  editNextStep: { note: "Next step", submit: "Save next step" },
  acknowledgeSourceChange: { note: "Reason (optional)", optionalNote: true, submit: "Acknowledge change" },
} satisfies Record<EditorKind, {
  priority?: boolean; severity?: boolean; assignee?: "optional" | "required" | "status";
  note?: string; optionalNote?: boolean; submit: string;
}>;

export function IssueDecisionEditor({ kind, triage, members, bug, busy, allowed, cancel, submit, formRef }: {
  kind: EditorKind; triage: Schema["Triage"]; members: Schema["Member"][] | undefined; bug: boolean;
  busy: boolean; allowed: boolean; cancel?: () => void; submit: (draft: DecisionDraft) => void;
  formRef?: Ref<HTMLFormElement>;
}) {
  const config: {
    priority?: boolean; severity?: boolean; assignee?: "optional" | "required" | "status";
    note?: string; optionalNote?: boolean; submit: string;
  } = fields[kind];
  const mayUnassign = config.assignee === "optional" || (config.assignee === "status" && triage.status === "ready");
  const showSeverity = config.severity || (kind === "accept" && bug);
  const noteId = useId();

  function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!allowed || busy) return;
    const form = new FormData(event.currentTarget);
    const value = (key: string) => String(form.get(key) ?? "").trim();
    const note = value("note");
    const assigneeId = value("assignee") || null;
    const priority = value("priority") as Schema["Accept"]["priority"];
    const severity = (value("severity") || null) as Schema["ChangeSeverity"]["severity"];
    if (config.note && !config.optionalNote && !note) {
      const field = event.currentTarget.elements.namedItem("note") as HTMLTextAreaElement;
      field.setCustomValidity("Enter " + config.note.toLowerCase() + ".");
      field.reportValidity();
      return;
    }
    switch (kind) {
      case "accept": submit({ kind, priority, nextStep: note, assigneeId, ...(severity ? { severity } : {}) }); break;
      case "requestInformation": if (assigneeId) submit({ kind, assigneeId, question: note }); break;
      case "answer": submit({ kind, answer: note }); break;
      case "setAside": submit({ kind, reason: note }); break;
      case "returnToReview":
      case "acknowledgeSourceChange": submit({ kind, ...(note ? { reason: note } : {}) }); break;
      case "reassign": submit({ kind, assigneeId }); break;
      case "changePriority": submit({ kind, priority }); break;
      case "changeSeverity": submit({ kind, severity }); break;
      case "editNextStep": submit({ kind, nextStep: note }); break;
    }
  }

  return <form ref={formRef} onSubmit={save} className="decision-form" aria-label={config.submit}>
    {config.priority && <label>Priority <select name="priority" defaultValue={triage.priority ?? "normal"} disabled={busy}>
      <option value="high">High</option><option value="normal">Normal</option><option value="low">Low</option>
    </select></label>}
    {config.assignee && (members ? <label>Assignee <select name="assignee" defaultValue={triage.assigneeId ?? ""}
      required={!mayUnassign} disabled={busy}>
      <option value="" disabled={!mayUnassign}>{mayUnassign ? "Leave unassigned" : "Select a teammate"}</option>
      {members.map(member => <option key={member.userId} value={member.userId}>{member.displayName}</option>)}
    </select></label> : <p className="hint">Loading teammate options…</p>)}
    {showSeverity && <label>Bug severity <select name="severity" defaultValue={triage.severity ?? ""} disabled={busy}>
      <option value="">No severity</option>
      {bug && <>
        <option value="critical">Critical</option><option value="major">Major</option>
        <option value="minor">Minor</option><option value="unknown">Unknown</option>
      </>}
    </select></label>}
    {config.note && <div className="decision-note">
      <label htmlFor={noteId}>{config.note}</label>
      <textarea id={noteId} name="note" rows={3} required={!config.optionalNote} maxLength={2000} disabled={busy}
        defaultValue={kind === "accept" || kind === "editNextStep" ? triage.nextStep ?? "" : ""}
        onChange={event => event.target.setCustomValidity("")} />
    </div>}
    {!allowed && <p className="hint">This action is unavailable for the current decision. Your draft is still here.</p>}
    <div className="decision-buttons">
      <button className="primary" disabled={busy || !allowed || Boolean(config.assignee && !members)}>
        {config.submit}</button>
      {cancel && <button type="button" disabled={busy} onClick={cancel}>Cancel</button>}
    </div>
  </form>;
}
