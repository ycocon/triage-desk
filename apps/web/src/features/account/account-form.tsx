"use client";

import Link from "next/link";
import { useEffect, useState, type FormEvent } from "react";
import { ApiError, api, messageFor, type Schema } from "@/lib/http";
import { Brand } from "./brand";
import { invitationStorageKey } from "./invitation-session";

type Mode = "sign-in" | "register" | "verify-email";
const copy = {
  "sign-in": { title: "Sign in", description: "Open your private workspace.", action: "Sign in" },
  "register": { title: "Create an account", description: "Use your email to set up access to Triage Desk.", action: "Create account" },
  "verify-email": { title: "Verify your email", description: "Open the link sent to your email or request a new one.", action: "Send verification email" },
};

export function AccountForm({ mode }: { mode: Mode }) {
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [confirmed, setConfirmed] = useState(false);
  const [proof, setProof] = useState<Schema["ConfirmEmail"] | null>(null);
  const [email, setEmail] = useState("");
  const content = copy[mode];

  useEffect(() => {
    if (mode !== "verify-email") return;
    const fragment = new URLSearchParams(window.location.hash.slice(1));
    const userId = fragment.get("userId");
    const token = fragment.get("token");
    if (userId && token) {
      window.history.replaceState(null, "", window.location.pathname);
      setProof({ userId, token });
    }
    setEmail(window.sessionStorage.getItem("verification-email") ?? "");
  }, [mode]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) return;
    setPending(true);
    setError("");
    setNotice("");
    const form = new FormData(event.currentTarget);
    const address = String(form.get("email") ?? "");
    const password = String(form.get("password") ?? "");
    try {
      if (mode === "sign-in") {
        const body: Schema["Login"] = { email: address, password };
        await api("/auth/login", { method: "POST", body: JSON.stringify(body) });
        window.location.replace(window.sessionStorage.getItem(invitationStorageKey) ? "/join" : "/workspaces");
      } else if (mode === "register") {
        const displayName = String(form.get("displayName") ?? "").trim();
        const body: Schema["Register"] = { email: address, password, ...(displayName && { displayName }) };
        await api("/auth/register", { method: "POST", body: JSON.stringify(body) });
        window.sessionStorage.setItem("verification-email", address);
        window.location.assign("/verify-email");
      } else if (proof) {
        await api("/auth/email-verification", { method: "POST", body: JSON.stringify(proof) });
        window.sessionStorage.removeItem("verification-email");
        setConfirmed(true);
      } else {
        const body: Schema["EmailRequest"] = { email: address };
        await api("/auth/email-verification/requests", { method: "POST", body: JSON.stringify(body) });
        setNotice("Check your inbox for a verification link if this address is eligible.");
      }
    } catch (failure) {
      setError(messageFor(failure));
      if (proof && failure instanceof ApiError && failure.status === 410) setProof(null);
    } finally {
      setPending(false);
    }
  }

  return (
    <main className="account-layout">
      <aside className="account-aside">
        <Brand />
        <div className="account-art" aria-hidden="true">
          <div className="art-orbit" /><div className="art-orbit art-orbit-inner" />
          <div className="art-queue">
            <div><span /><b /><i /></div><div><span /><b /><i /></div><div><span /><b /><i /></div>
          </div>
        </div>
        <p>Private triage for public GitHub issues.</p>
      </aside>
      <div className="account-main">
        <div className="account-card">
          <div className="mobile-brand"><Brand /></div>
          <h1>{confirmed ? "Email verified" : content.title}</h1>
          <p className="hint">{confirmed ? "Sign in to open your workspaces." : proof ? "Confirm that this email belongs to you." : content.description}</p>
          {confirmed ? <Link href="/sign-in" className="button primary">Continue to sign in</Link> :
            <form onSubmit={submit} aria-busy={pending}>
              {mode === "register" && <label>Display name <span className="optional">(optional)</span>
                <input name="displayName" autoComplete="name" maxLength={80} /></label>}
              {!proof && <label>Email<input name="email" type="email" autoComplete="email"
                value={email} onChange={event => setEmail(event.target.value)} required maxLength={254} /></label>}
              {mode !== "verify-email" && <label>Password<input name="password" type="password"
                autoComplete={mode === "register" ? "new-password" : "current-password"} required
                minLength={mode === "register" ? 12 : 1} maxLength={128}
                aria-describedby={mode === "register" ? "password-length" : undefined} /></label>}
              {mode === "register" && <p id="password-length" className="field-note">Use at least 12 characters.</p>}
              {error && <p className="error" role="alert">{error}</p>}
              {notice && <p className="notice" role="status">{notice}</p>}
              <button className="primary" disabled={pending}>{pending ? "Please wait…" : proof ? "Confirm email" : content.action}</button>
            </form>}
          {!confirmed && (mode === "sign-in" ?
            <div className="account-links"><Link href="/register">Create an account</Link><Link href="/verify-email">Resend verification</Link></div> :
            <div className="account-links"><Link href="/sign-in">Back to sign in</Link></div>)}
        </div>
      </div>
    </main>
  );
}
