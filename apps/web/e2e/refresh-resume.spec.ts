import { expect, test } from "@playwright/test";
import { resolve } from "node:path";
import { registerAndVerify } from "./accounts";
import { postDecision, teammate } from "./triage-fixture";

test("a Reviewer resumes a three-page refresh after the Owner closes the tab", async ({ page, context, browser }) => {
  await registerAndVerify(page, "Ari", undefined, undefined, false);
  const name = "Northline Refresh";
  const workspace = await postDecision(page.request, "/workspaces", { name }, 201);
  const path = "/workspaces/" + workspace.workspaceId;
  const repoPath = path + "/repositories";
  const repo = await postDecision(page.request, repoPath, { fullName: "northline/refresh" }, 201);
  const runPath = "/api/v1" + repoPath + "/" + repo.repositoryId + "/sync-runs/";
  const reviewerContext = await browser.newContext({ baseURL: "https://localhost:3000", ignoreHTTPSErrors: true });

  try {
    const reviewer = await teammate(page, reviewerContext, path, name, "Mara", "reviewer", false);
    const refreshTab = await context.newPage();
    let holdNextPage!: () => void;
    const nextPageRequested = new Promise<void>(resolve => { holdNextPage = resolve; });
    await refreshTab.route("**/sync-runs/*/advance", async route => {
      if (route.request().postDataJSON().expectedCheckpoint === 1) {
        await route.abort("aborted");
        holdNextPage();
        return;
      }
      await route.continue();
    });
    await refreshTab.goto(repoPath);
    await refreshTab.getByRole("button", { name: "Refresh issues" }).click();
    await nextPageRequested;
    await refreshTab.close();

    const connections = await page.request.get("/api/v1" + repoPath);
    const [saved] = (await connections.json()).items;
    expect(saved.openRunId).toBeTruthy();
    expect(saved.lastSuccessAt).toBeNull();
    const savedResponse = await page.request.get(runPath + saved.openRunId);
    expect(await savedResponse.json()).toMatchObject({ checkpoint: 1, imported: 1 });

    await reviewer.goto(repoPath);
    const resume = reviewer.getByRole("button", { name: "Resume refresh" });
    await expect(reviewer.getByText("Checkpoint saved", { exact: true })).toBeVisible();
    await expect(reviewer.getByText(/1 examined · 1 imported/)).toBeVisible();
    if (process.env.TRIAGEDESK_RESUME_SCREENSHOT)
      await reviewer.screenshot({ path: resolve(process.env.TRIAGEDESK_RESUME_SCREENSHOT), fullPage: true });
    await resume.click();
    await expect(reviewer.getByText("Refresh complete.", { exact: true })).toBeVisible();
    const finished = await reviewer.request.get(runPath + saved.openRunId);
    expect(await finished.json()).toMatchObject({ runId: saved.openRunId, state: "succeeded", checkpoint: 3, imported: 3 });
    const updatedConnections = await reviewer.request.get("/api/v1" + repoPath);
    expect((await updatedConnections.json()).items[0].lastSuccessAt).toBeTruthy();
    await reviewer.getByRole("link", { name: "Open triage inbox", exact: true }).click();
    await expect(reviewer.getByRole("heading", { name: "Needs review · 3" })).toBeVisible();
    for (const number of [1, 2, 3])
      await expect(reviewer.getByRole("link", { name: new RegExp(`Refresh fixture issue ${number}`) })).toHaveCount(1);
  } finally {
    await reviewerContext.close();
  }
});
