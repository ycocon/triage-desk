import { localId, pageNumber } from "@/features/workspaces/inbox-query";
import { WorkspaceShell } from "@/features/workspaces/workspace-shell";

export default async function Activity({ params, searchParams }: {
  params: Promise<{ workspaceId: string }>;
  searchParams: Promise<{ page?: string | string[]; repositoryId?: string | string[] }>;
}) {
  const [{ workspaceId }, query] = await Promise.all([params, searchParams]);
  return <WorkspaceShell key={workspaceId} workspaceId={workspaceId} view="activity"
    activityQuery={{ page: pageNumber(query.page), repositoryId: localId(query.repositoryId) }} />;
}
