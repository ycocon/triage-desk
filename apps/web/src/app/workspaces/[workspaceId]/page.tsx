import { WorkspaceShell } from "@/features/workspaces/workspace-shell";

export default async function Workspace({ params }: { params: Promise<{ workspaceId: string }> }) {
  const { workspaceId } = await params;
  return <WorkspaceShell key={workspaceId} workspaceId={workspaceId} />;
}
