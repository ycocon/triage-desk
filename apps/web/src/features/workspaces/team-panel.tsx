"use client";

import { useEffect, useState, type FormEvent } from "react";
import { ApiError, api, messageFor, type Schema } from "@/lib/http";

type Member = Schema["Member"];
type Workspace = Schema["Workspace"];
type Action =
  | { kind: "remove" | "transfer"; member: Member }
  | { kind: "leave" }
  | { kind: "revoke"; invite: Schema["Invitation"] };

const emptyMembers: Schema["MemberPage"] = { items: [], page: 1, pageSize: 25, total: 0 };
const emptyInvites: Schema["InvitationPage"] = { items: [], page: 1, pageSize: 25, total: 0 };
const formatDate = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

export function TeamPanel({ workspace, account }: { workspace: Workspace; account: Schema["User"] }) {
  const path = "/workspaces/" + workspace.workspaceId;
  const owner = workspace.myRole === "owner";
  const [members, setMembers] = useState(emptyMembers);
  const [invites, setInvites] = useState(emptyInvites);
  const [memberPage, setMemberPage] = useState(1);
  const [invitePage, setInvitePage] = useState(1);
  const [reload, setReload] = useState(0);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [confirm, setConfirm] = useState<Action>();
  const [acceptUrl, setAcceptUrl] = useState("");
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    const options = { signal: controller.signal };
    Promise.all([
      api<Schema["MemberPage"]>(path + "/members?page=" + memberPage, options),
      owner ? api<Schema["InvitationPage"]>(path + "/invitations?page=" + invitePage, options) : emptyInvites,
    ]).then(([memberList, inviteList]) => {
      if (memberPage > 1 && !memberList.items.length) setMemberPage(memberPage - 1);
      else setMembers(memberList);
      if (invitePage > 1 && !inviteList.items.length) setInvitePage(invitePage - 1);
      else setInvites(inviteList);
    }).catch(failure => {
      if (controller.signal.aborted) return;
      if (failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
        window.location.replace(failure.status === 401 ? "/sign-in" : path);
        return;
      }
      setError(messageFor(failure));
    }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [path, owner, memberPage, invitePage, reload]);

  async function run(action: () => Promise<void>, refreshLists = true) {
    if (busy) return;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await action();
      setConfirm(undefined);
      if (refreshLists) setReload(value => value + 1);
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 401) window.location.replace("/sign-in");
      else {
        // Foreign targets can also return 404. Refresh the authorized lists before retrying.
        setError(messageFor(failure));
        setReload(value => value + 1);
      }
    } finally {
      setBusy(false);
    }
  }

  function inviteMember(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const fields = new FormData(form);
    const email = String(fields.get("email")).trim();
    const role = String(fields.get("role")) as Schema["Invite"]["role"];
    setAcceptUrl("");
    void run(async () => {
      const body: Schema["Invite"] = { email, role };
      const result = await api<Schema["NewInvitation"]>(path + "/invitations",
        { method: "POST", body: JSON.stringify(body) });
      form.reset();
      setAcceptUrl(result.acceptUrl);
      setInvitePage(1);
      setNotice("Invitation created.");
    });
  }

  function updateRole(member: Member, role: Schema["ChangeRole"]["role"]) {
    if (member.role === role) return;
    void run(async () => {
      await api(path + "/members/" + member.userId, { method: "PATCH", body: JSON.stringify({ role }) });
      setNotice("Role updated.");
    });
  }

  function finishConfirmation(action: Action) {
    void run(async () => {
      if (action.kind === "transfer") {
        await api(path + "/ownership", { method: "PUT", body: JSON.stringify({ newOwnerId: action.member.userId }) });
        window.location.reload();
      } else if (action.kind === "leave") {
        await api(path + "/membership", { method: "DELETE" });
        window.location.replace("/workspaces");
      } else if (action.kind === "revoke") {
        await api(path + "/invitations/" + action.invite.invitationId, { method: "DELETE" });
        setAcceptUrl("");
        setNotice("Invitation revoked.");
      } else {
        await api(path + "/members/" + action.member.userId, { method: "DELETE" });
        setNotice("Teammate removed.");
      }
    }, action.kind !== "transfer" && action.kind !== "leave");
  }

  return <>
    <div className="list-heading"><div><h1>Team</h1><p className="hint">Manage access to {workspace.name}.</p></div></div>
    {owner && <section className="team-section" aria-labelledby="invite-heading">
      <h2 id="invite-heading">Invite a teammate</h2>
      <form className="team-invite-form" onSubmit={inviteMember}>
        <label>Email address<input name="email" type="email" required maxLength={254} autoComplete="off" /></label>
        <label>Invitation role<select name="role"><option value="member">Member</option><option value="reviewer">Reviewer</option></select></label>
        <button className="primary" disabled={busy}>Create invitation</button>
      </form>
      {acceptUrl && <div className="invitation-result">
        <label>Private invitation link<input readOnly value={acceptUrl} onFocus={event => event.target.select()} /></label>
        <button onClick={() => void navigator.clipboard.writeText(acceptUrl).then(
          () => setNotice("Private link copied."),
          () => setNotice("Select the link and copy it to share."),
        )}>Copy invitation link</button>
        <p className="hint">Share this link with the invited person. It expires in seven days.</p>
      </div>}
    </section>}

    <section className="team-section" aria-labelledby="member-heading">
      <h2 id="member-heading">Members</h2>
      {loading ? <p className="hint" role="status">Loading team…</p> : <ul className="team-rows">
        {members.items.map(member => {
          const self = member.userId === account.userId;
          return <li className="team-row" key={member.userId}>
            <span className="avatar" aria-hidden="true">{member.displayName.slice(0, 1)}</span>
            <div className="team-person"><strong>{member.displayName}</strong><span className="role">{member.role}{self && " · You"}</span></div>
            {owner && member.role !== "owner" && <div className="team-actions">
              <select aria-label={"Role for " + member.displayName} value={member.role} disabled={busy} onChange={event =>
                updateRole(member, event.target.value as Schema["ChangeRole"]["role"])}>
                <option value="member">Member</option><option value="reviewer">Reviewer</option>
              </select>
              <button disabled={busy} onClick={() => setConfirm({ kind: "transfer", member })}>Transfer ownership</button>
              <button disabled={busy} onClick={() => setConfirm({ kind: "remove", member })}>Remove</button>
            </div>}
          </li>;
        })}
      </ul>}
      <TeamPages name="Member pages" page={members} change={setMemberPage} />
      {!owner && <button disabled={busy} className="leave-workspace" onClick={() => setConfirm({ kind: "leave" })}>Leave workspace</button>}
    </section>

    {owner && <section className="team-section" aria-labelledby="pending-heading">
      <h2 id="pending-heading">Invitations</h2>
      {invites.items.length ? <ul className="team-rows">
        {invites.items.map(invite => <li className="team-row" key={invite.invitationId}>
          <div className="team-person"><strong>{invite.email}</strong>
            <span className="role">{invite.role} · {invite.state}</span>
            {invite.state === "pending" && <small>Expires {formatDate.format(new Date(invite.expiresAt))}</small>}
          </div>
          {invite.state === "pending" && <button disabled={busy} onClick={() => setConfirm({ kind: "revoke", invite })}>Revoke</button>}
        </li>)}
      </ul> : !loading && <p className="hint">No invitations yet.</p>}
      <TeamPages name="Invitation pages" page={invites} change={setInvitePage} />
    </section>}
    {notice && <p className="notice team-message" role="status">{notice}</p>}
    {error && <p className="error team-message" role="alert">{error}</p>}
    {confirm && <Confirmation action={confirm} busy={busy} cancel={() => setConfirm(undefined)}
      submit={() => finishConfirmation(confirm)} error={error} />}
  </>;
}

function TeamPages({ name, page, change }: { name: string; page: { page: number; pageSize: number; total: number };
  change: (page: number) => void }) {
  if (page.total <= page.pageSize) return null;
  return <nav className="pages" aria-label={name}>
    <button disabled={page.page === 1} onClick={() => change(page.page - 1)}>Previous</button>
    <span>Page {page.page}</span>
    <button disabled={page.page * page.pageSize >= page.total} onClick={() => change(page.page + 1)}>Next</button>
  </nav>;
}

function Confirmation({ action, busy, cancel, submit, error }: {
  action: Action; busy: boolean; cancel: () => void; submit: () => void; error: string;
}) {
  const [dialog, setDialog] = useState<HTMLDialogElement | null>(null);
  useEffect(() => { if (dialog && !dialog.open) dialog.showModal(); }, [dialog]);
  const content = action.kind === "leave" ? ["Leave workspace?", "You’ll need a new invitation to join again.", "Leave workspace"] :
    action.kind === "revoke" ? ["Revoke invitation?", "The link for " + action.invite.email + " will stop working.", "Revoke invitation"] :
    action.kind === "transfer" ? ["Transfer ownership?", action.member.displayName + " will become the Owner. You’ll return to your previous role.", "Confirm transfer"] :
    ["Remove teammate?", action.member.displayName + " will lose access to this workspace on their next request.", "Remove teammate"];

  return <dialog ref={setDialog} className="team-dialog" aria-labelledby="team-confirm-heading"
    onCancel={event => { event.preventDefault(); if (!busy) cancel(); }}>
    <h2 id="team-confirm-heading">{content[0]}</h2><p className="hint">{content[1]}</p>
    {error && <p className="error" role="alert">{error}</p>}
    <div className="form-actions">
      <button autoFocus disabled={busy} onClick={cancel}>Cancel</button>
      <button className="primary" disabled={busy} onClick={submit}>{content[2]}</button>
    </div>
  </dialog>;
}
