"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { ApiError, api, messageFor, type Schema } from "@/lib/http";
import { Brand } from "./brand";
import { invitationStorageKey } from "./invitation-session";

export function JoinWorkspace() {
  const [token, setToken] = useState("");
  const [invitation, setInvitation] = useState<Schema["InvitationPreview"]>();
  const [loading, setLoading] = useState(true);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    const fragment = new URLSearchParams(window.location.hash.slice(1));
    if (fragment.has("token")) {
      const value = fragment.get("token") ?? "";
      if (/^[A-Za-z0-9_-]{43}$/.test(value)) window.sessionStorage.setItem(invitationStorageKey, value);
      else window.sessionStorage.removeItem(invitationStorageKey);
      window.history.replaceState(null, "", "/join");
    }

    const proof = window.sessionStorage.getItem(invitationStorageKey) ?? "";
    if (!proof) {
      setError("Open the private link shared by the workspace Owner.");
      setLoading(false);
      return () => controller.abort();
    }
    setToken(proof);
    api<Schema["InvitationPreview"]>("/invitations/preview", {
      method: "POST", body: JSON.stringify({ token: proof }), signal: controller.signal,
    }).then(setInvitation).catch(failure => {
      if (controller.signal.aborted) return;
      if (failure instanceof ApiError && failure.status === 401) {
        window.location.replace("/sign-in");
      } else if (failure instanceof ApiError && failure.status === 404) {
        setError("This invitation is unavailable for this account.");
      } else {
        setError(messageFor(failure));
      }
    }).finally(() => {
      if (!controller.signal.aborted) setLoading(false);
    });
    return () => controller.abort();
  }, []);

  async function accept() {
    setPending(true);
    setError("");
    try {
      const result = await api<Schema["Workspace"]>("/invitations/accept", {
        method: "POST", body: JSON.stringify({ token }),
      });
      window.sessionStorage.removeItem(invitationStorageKey);
      window.location.replace("/workspaces/" + result.workspaceId + "/team");
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 401) window.location.replace("/sign-in");
      else setError(messageFor(failure));
    } finally {
      setPending(false);
    }
  }

  async function switchAccount() {
    setPending(true);
    setError("");
    try {
      await api("/auth/logout", { method: "POST" });
      window.location.replace("/sign-in");
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 401) window.location.replace("/sign-in");
      else setError(messageFor(failure));
    } finally {
      setPending(false);
    }
  }

  return <main className="standalone join-page">
    <Brand />
    <h1>{invitation ? "Join " + invitation.workspaceName : "Workspace invitation"}</h1>
    {loading && <p role="status">Checking invitation…</p>}
    {invitation && <>
      <p className="hint">You’ve been invited as <strong className="role">{invitation.role}</strong>.</p>
      <button className="primary" onClick={() => void accept()} disabled={pending}>Accept invitation</button>
    </>}
    {error && <p className="error" role="alert">{error}</p>}
    {!loading && token && <button onClick={() => void switchAccount()} disabled={pending}>Use a different account</button>}
    {!loading && <Link href="/workspaces" onClick={() => window.sessionStorage.removeItem(invitationStorageKey)}>Back to workspaces</Link>}
  </main>;
}
