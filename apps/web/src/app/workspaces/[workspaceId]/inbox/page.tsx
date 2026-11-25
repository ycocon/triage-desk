import { WorkspaceShell } from "@/features/workspaces/workspace-shell";

export default async function Inbox({ params, searchParams }: {
  params: Promise<{ workspaceId: string }>;
  searchParams: Promise<{ issue?: string | string[]; page?: string | string[];
    view?: string | string[]; status?: string | string[] }>;
}) {
  const [{ workspaceId }, { issue, page, view, status }] = await Promise.all([params, searchParams]);
  const number = typeof page === "string" ? Number(page) : 1;
  const inboxPage = Number.isSafeInteger(number) && number >= 1 && number <= 2147483647 ? number : 1;
  const inboxView = view === "available" || view === "myWork" || view === "all" ? view : "needsReview";
  return <WorkspaceShell key={workspaceId} workspaceId={workspaceId} view="inbox"
    issueId={typeof issue === "string" ? issue : undefined} inboxPage={inboxPage}
    inboxView={inboxView} inboxDone={inboxView === "all" && status === "done"} />;
}
