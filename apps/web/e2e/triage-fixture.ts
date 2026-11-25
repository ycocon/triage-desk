import { expect, type APIRequestContext, type BrowserContext, type Page } from "@playwright/test";
import { registerAndVerify, testEmail } from "./accounts";

export async function postDecision(request: APIRequestContext, path: string, data: object, status: number) {
  const pair = await request.get("/api/v1/auth/csrf");
  const response = await request.post("/api/v1" + path, {
    data, headers: { "X-CSRF-Token": (await pair.json()).requestToken },
  });
  expect(response.status()).toBe(status);
  return response.json();
}

export async function importedWorkspace(owner: Page, name: string) {
  await registerAndVerify(owner, "Ari");
  const workspace = await postDecision(owner.request, "/workspaces", { name }, 201);
  const path = "/workspaces/" + workspace.workspaceId;
  const repo = await postDecision(owner.request, path + "/repositories", { fullName: "northline/cli" }, 201);
  const runs = path + "/repositories/" + repo.repositoryId + "/sync-runs";
  const run = await postDecision(owner.request, runs, { runId: crypto.randomUUID() }, 201);
  await postDecision(owner.request, runs + "/" + run.runId + "/advance", { expectedCheckpoint: 0 }, 200);
  const complete = await postDecision(owner.request, runs + "/" + run.runId + "/advance", { expectedCheckpoint: 1 }, 200);
  expect(complete.state).toBe("succeeded");
  return path;
}

export async function teammate(owner: Page, context: BrowserContext, path: string,
  workspaceName: string, name: string, role: "member" | "reviewer", checkUnverified = true) {
  const email = testEmail(name);
  const invitation = await postDecision(owner.request, path + "/invitations", { email, role }, 201);
  const page = await context.newPage();
  await page.goto(invitation.acceptUrl);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await registerAndVerify(page, name, email, "Join " + workspaceName, checkUnverified);
  await page.getByRole("button", { name: "Accept invitation" }).click();
  await expect(page.getByRole("heading", { name: "Team" })).toBeVisible();
  return page;
}
