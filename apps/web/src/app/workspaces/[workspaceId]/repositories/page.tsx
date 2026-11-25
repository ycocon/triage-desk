import { WorkspaceShell } from "@/features/workspaces/workspace-shell";

export default async function Repositories({ params }: { params: Promise<{ workspaceId: string }> }) {
  const { workspaceId } = await params;
  return <WorkspaceShell key={workspaceId} workspaceId={workspaceId} view="repositories" />;
}
