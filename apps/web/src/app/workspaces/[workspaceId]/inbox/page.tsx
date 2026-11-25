import { WorkspaceShell } from "@/features/workspaces/workspace-shell";
import { readInboxQuery } from "@/features/workspaces/inbox-query";

export default async function Inbox({ params, searchParams }: {
  params: Promise<{ workspaceId: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const [{ workspaceId }, filters] = await Promise.all([params, searchParams]);
  return <WorkspaceShell key={workspaceId} workspaceId={workspaceId} view="inbox" inboxQuery={readInboxQuery(filters)} />;
}
