"use client";

import Link from "next/link";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { ApiError, api, messageFor, type Schema } from "@/lib/http";

type Repository = Schema["Repository"];
type Run = Schema["Run"];
const stamp = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

export function RepositoryPanel({ workspace }: { workspace: Schema["Workspace"] }) {
  const path = "/workspaces/" + workspace.workspaceId;
  const owner = workspace.myRole === "owner";
  const canRefresh = workspace.myRole !== "member";
  const [repos, setRepos] = useState<Schema["RepositoryPage"]>();
  const [page, setPage] = useState(1);
  const [reload, setReload] = useState(0);
  const [name, setName] = useState("");
  const [preview, setPreview] = useState<Schema["RepositoryPreview"]>();
  const [busy, setBusy] = useState(false);
  const [refreshing, setRefreshing] = useState<string>();
  const [run, setRun] = useState<Run>();
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const stop = useRef({ requested: false, alive: true });

  useEffect(() => {
    stop.current.alive = true;
    return () => { stop.current.alive = false; };
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    const view = owner ? "&connection=all" : "";
    api<Schema["RepositoryPage"]>(path + "/repositories?page=" + page + view,
      { signal: controller.signal }).then(setRepos).catch(failure => {
      if (!controller.signal.aborted) setError(messageFor(failure));
    });
    return () => controller.abort();
  }, [path, owner, page, reload]);

  async function manage(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setNotice("");
    try { await action(); }
    catch (failure) { setError(messageFor(failure)); }
    finally { setBusy(false); }
  }

  function previewRepository(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPreview(undefined);
    void manage(async () => setPreview(await api<Schema["RepositoryPreview"]>(path + "/repository-previews", {
      method: "POST", body: JSON.stringify({ fullName: name.trim() }),
    })));
  }

  async function connect(fullName: string) {
    await manage(async () => {
      await api(path + "/repositories", { method: "POST", body: JSON.stringify({ fullName }) });
      setPreview(undefined);
      setName("");
      setPage(1);
      setReload(value => value + 1);
      setNotice(fullName + " connected.");
    });
  }

  async function disconnect(repo: Repository) {
    if (!window.confirm("Disconnect " + repo.fullName + "? Its issues will be hidden from the inbox.")) return;
    await manage(async () => {
      await api(path + "/repositories/" + repo.repositoryId, { method: "DELETE" });
      setRun(current => current?.repositoryId === repo.repositoryId ? undefined : current);
      setReload(value => value + 1);
      setNotice(repo.fullName + " disconnected.");
    });
  }

  async function refresh(repo: Repository) {
    const location = path + "/repositories/" + repo.repositoryId;
    const runs = location + "/sync-runs";
    setRefreshing(repo.repositoryId);
    setError("");
    setNotice("");
    stop.current.requested = false;
    let runId = repo.openRunId;
    try {
      let progress: Run;
      if (runId) progress = await api<Run>(runs + "/" + runId);
      else {
        try {
          progress = await api<Run>(runs, { method: "POST", body: JSON.stringify({ runId: crypto.randomUUID() }) });
        } catch (failure) {
          if (!(failure instanceof ApiError) || failure.code !== "open_run_exists") throw failure;
          const current = await api<Repository>(location);
          if (!current.openRunId) throw failure;
          progress = await api<Run>(runs + "/" + current.openRunId);
        }
      }
      runId = progress.runId;
      setRun(progress);
      while (stop.current.alive && (progress.state === "running" || progress.state === "paused")) {
        const action = stop.current.requested ? "pause" : "advance";
        const next: Run = await api<Run>(runs + "/" + runId + "/" + action, {
          method: "POST", body: JSON.stringify({ expectedCheckpoint: progress.checkpoint }),
        });
        setRun(next);
        if (action === "pause") break;
        if (next.checkpoint <= progress.checkpoint && next.state === "running")
          throw new Error("The saved checkpoint did not advance.");
        progress = next;
      }
      if (progress.state === "succeeded") setNotice("Refresh complete.");
    } catch (failure) {
      setError(messageFor(failure));
      if (runId && stop.current.alive) {
        try { setRun(await api<Run>(runs + "/" + runId)); }
        catch { /* The latest repository list will reflect any access change. */ }
      }
    } finally {
      setRefreshing(undefined);
      setReload(value => value + 1);
    }
  }

  return <>
    <div className="list-heading"><div><h1>Repositories</h1><p className="hint">Connect public GitHub repositories and refresh their open issues.</p></div>
      <Link href={path + "/inbox"}>Open triage inbox</Link></div>
    {owner && <section className="team-section" aria-labelledby="connect-heading">
      <h2 id="connect-heading">Connect a repository</h2>
      <form className="repository-form" onSubmit={previewRepository}>
        <label>GitHub repository<input value={name} onChange={event => { setName(event.target.value); setPreview(undefined); }}
          required maxLength={140} placeholder="owner/repository" autoCapitalize="none" /></label>
        <button disabled={busy || Boolean(refreshing)}>Preview repository</button>
      </form>
      {preview && <div className="repository-preview">
        <div><a href={preview.webUrl} target="_blank" rel="noreferrer">{preview.fullName}</a><p className="hint">Public repository</p></div>
        <button className="primary" disabled={busy || Boolean(refreshing)} onClick={() => void connect(preview.fullName)}>Connect repository</button>
      </div>}
    </section>}
    <section className="team-section" aria-labelledby="repository-list-heading">
      <h2 id="repository-list-heading">Workspace repositories</h2>
      {!repos ? <p role="status" className="hint">Loading repositories…</p> :
        repos.items.length ? <ul className="team-rows">
          {repos.items.map(repo => <li key={repo.repositoryId} className="team-row repository-row">
            <div className="team-person"><strong>{repo.fullName}</strong>
              <span>{repo.connection === "disconnected" ? "Disconnected" : repo.visibility === "public" ? "Public · Connected" : "Source unavailable"}</span>
              <small>{repo.lastSuccessAt ? "Last successful refresh " + stamp.format(new Date(repo.lastSuccessAt)) : "Never refreshed successfully"}</small>
              {run?.repositoryId === repo.repositoryId ? <small role="status">
                <span className="role">{run.state === "succeeded" ? "Complete" : run.state}</span> · {run.examined} examined · {run.imported} imported · {run.updated} updated
                {run.lastErrorCode && (run.lastErrorCode === "source_unavailable" ? " · Source no longer available" :
                  run.lastErrorCode === "rate_limited" ? " · GitHub rate limit" : " · GitHub request failed")}
                {run.retryAt && " · Retry after " + stamp.format(new Date(run.retryAt))}
              </small> : repo.openRunId && <small>Refresh checkpoint saved</small>}
            </div>
            <div className="team-actions">
              {repo.connection === "connected" ? <>
                {canRefresh && (refreshing === repo.repositoryId
                  ? <button onClick={() => { stop.current.requested = true; }}>Pause refresh</button>
                  : <button className="primary" disabled={Boolean(refreshing) || busy} onClick={() => void refresh(repo)}>
                    {repo.openRunId ? "Resume refresh" : "Refresh issues"}</button>)}
                {owner && <button disabled={Boolean(refreshing) || busy} onClick={() => void disconnect(repo)}>Disconnect</button>}
              </> : owner && <button disabled={Boolean(refreshing) || busy} onClick={() => void connect(repo.fullName)}>Reconnect</button>}
            </div>
          </li>)}
        </ul> : <p className="hint">No repositories connected.</p>}
      {repos && repos.total > repos.pageSize && <nav className="pages" aria-label="Repository pages">
        <button disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {repos.page}</span>
        <button disabled={page * repos.pageSize >= repos.total} onClick={() => setPage(page + 1)}>Next</button>
      </nav>}
    </section>
    {notice && <p className="notice team-message" role="status">{notice}</p>}
    {error && <p className="error team-message" role="alert">{error}</p>}
  </>;
}
