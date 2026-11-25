"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { api, type Schema } from "@/lib/http";
import { inboxHref } from "./inbox-query";

export function ClosedAssignmentsNotice({ path, revision }: { path: string; revision: number }) {
  const [count, setCount] = useState(0);
  useEffect(() => {
    let request: AbortController | undefined;
    function load() {
      request?.abort();
      const controller = new AbortController();
      request = controller;
      api<Schema["IssueSummaryPage"]>(path + "/issues?view=myWork&githubState=closed&pageSize=1",
        { signal: controller.signal })
        .then(result => { if (!controller.signal.aborted) setCount(result.total); })
        .catch(() => { if (!controller.signal.aborted) setCount(0); });
    }
    load();
    window.addEventListener("focus", load);
    return () => {
      request?.abort();
      window.removeEventListener("focus", load);
    };
  }, [path, revision]);

  return <div aria-live="polite">
    {count > 0 && <p className="closure-notice">
      {count === 1 ? "One issue assigned to you is closed on GitHub." : count + " issues assigned to you are closed on GitHub."}
      {" "}<Link href={inboxHref(path, { view: "myWork", page: 1, githubState: "closed" })}>View closed assignments</Link>
    </p>}
  </div>;
}
