import { expect, test } from "@playwright/test";
import { resolve } from "node:path";
import { registerAndVerify } from "./accounts";
import { postDecision, teammate } from "./triage-fixture";

test("dashboard opens matching queues and assigned Members see GitHub closure", async ({ page, browser }) => {
  await registerAndVerify(page, "Ari", undefined, undefined, false);
  const name = "Northline Queues";
  const workspace = await postDecision(page.request, "/workspaces", { name }, 201);
  const path = "/workspaces/" + workspace.workspaceId;
  const repo = await postDecision(page.request, path + "/repositories", { fullName: "northline/workload" }, 201);
  const runs = path + "/repositories/" + repo.repositoryId + "/sync-runs";
  const { runId } = await postDecision(page.request, runs, { runId: crypto.randomUUID() }, 201);
  await postDecision(page.request, runs + "/" + runId + "/advance", { expectedCheckpoint: 0 }, 200);

  const memberContext = await browser.newContext({ baseURL: "https://localhost:3000", ignoreHTTPSErrors: true });
  try {
    const member = await teammate(page, memberContext, path, name, "Jules", "member", false);
    const memberAccount = await member.request.get("/api/v1/me");
    const { userId } = await memberAccount.json();
    const list = await page.request.get("/api/v1" + path + "/issues");
    const { items } = await list.json();
    for (const { source, issueId } of items) {
      if (source.number === 44) continue;
      await postDecision(page.request, path + "/issues/" + issueId + "/commands", {
        kind: "accept", commandId: crypto.randomUUID(), expectedVersion: 1, priority: "high",
        nextStep: "Check against the latest release.", ...(source.number === 42 ? { assigneeId: userId } : {}),
      }, 200);
    }
    await member.goto(path);
    await expect(member.getByRole("heading", { name: "Issue queues" })).toBeVisible();
    await expect(member.getByRole("link", { name: "View closed assignments" })).toHaveCount(0);

    await page.goto(path);
    const counts = page.getByRole("region", { name: "Issue queues" });
    await expect(counts.getByRole("link", { name: "1 Needs review" })).toBeVisible();
    await counts.getByRole("link", { name: "1 Available to claim" }).click();
    await expect(page.getByRole("heading", { name: "Available to claim · 1" })).toBeVisible();
    await expect(page.getByRole("link", { name: /Add a profile example/ })).toBeVisible();
    await page.goBack();
    const workload = page.getByRole("region", { name: "Active workload" });
    await expect(workload.getByRole("link", { name: "Jules 1 active" })).toBeVisible();
    if (process.env.TRIAGEDESK_QUEUE_SCREENSHOT)
      await page.screenshot({ path: resolve(process.env.TRIAGEDESK_QUEUE_SCREENSHOT), fullPage: true });
    await workload.getByRole("link", { name: "Jules 1 active" }).click();
    const heading = page.getByRole("heading", { name: "Active work · 1" });
    await expect(heading).toBeVisible();
    const search = page.getByRole("searchbox", { name: "Search issues" });
    await search.fill("NORTHLINE/workload #42");
    await search.press("Enter");
    await expect(page).toHaveURL(/q=NORTHLINE%2Fworkload\+%2342/);
    await expect(heading).toBeFocused();
    await page.keyboard.press("Tab");
    const row = page.getByRole("link", { name: /CLI loses a saved profile/ });
    await expect(row).toBeFocused();
    await page.keyboard.press("Enter");
    const detail = page.getByRole("region", { name: "Issue detail" });
    await expect(detail.getByRole("link", { name: "Back to active work" })).toBeFocused();
    await expect(detail.getByRole("heading", { name: "CLI loses a saved profile" })).toBeVisible();
    await page.keyboard.press("Enter");
    await expect(row).toBeFocused();
    await expect(search).toHaveValue("NORTHLINE/workload #42");
    await expect(page.getByLabel("Assignee")).toHaveValue(userId);
    await search.fill("no matching fixture");
    await search.press("Enter");
    await expect(page.getByRole("heading", { name: "Active work · 0" })).toBeFocused();
    await expect(page.getByText("No issues match these filters.", { exact: true })).toBeVisible();
    await page.goBack();
    await expect(row).toBeVisible();
    await expect(search).toHaveValue("NORTHLINE/workload #42");

    await page.setViewportSize({ width: 390, height: 844 });
    const appearance = page.getByLabel("Appearance");
    await appearance.selectOption("dark");
    await expect(page.locator(".workspace-layout")).toHaveAttribute("data-theme", "dark");
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390);
    await row.click();
    await expect(detail.getByRole("heading", { name: "CLI loses a saved profile" })).toBeVisible();
    await expect(row).toBeHidden();
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390);
    await page.goBack();
    await expect(row).toBeFocused();
    await expect(search).toHaveValue("NORTHLINE/workload #42");
    await appearance.selectOption("light");
    await expect(page.locator(".workspace-layout")).toHaveAttribute("data-theme", "light");

    await page.getByRole("navigation", { name: "Workspace navigation" }).getByRole("link", { name: "Repositories" }).click();
    await page.getByRole("button", { name: "Refresh issues" }).click();
    await expect(page.getByText("Refresh complete.", { exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: "View closed assignments" })).toHaveCount(0);
    await member.reload();
    await expect(member.getByText("One issue assigned to you is closed on GitHub.")).toBeVisible();
    await member.getByRole("link", { name: "View closed assignments" }).click();
    await expect(member.getByRole("heading", { name: "My work · 1" })).toBeVisible();
    await expect(member.getByText("Assigned to you · Closed on GitHub")).toBeVisible();
    if (process.env.TRIAGEDESK_CLOSURE_SCREENSHOT)
      await member.screenshot({ path: resolve(process.env.TRIAGEDESK_CLOSURE_SCREENSHOT), fullPage: true });

    await page.getByRole("navigation", { name: "Workspace navigation" }).getByRole("link", { name: "Activity" }).click();
    await expect(page.getByText("flagged a GitHub state change", { exact: false })).toBeVisible();
    await page.getByLabel("Repository").selectOption(repo.repositoryId);
    await page.getByRole("button", { name: "Apply filter" }).click();
    await expect(page).toHaveURL(new RegExp("repositoryId=" + repo.repositoryId));
    await expect(page.getByRole("heading", { name: "Issue activity · 3" })).toBeVisible();

    await page.getByRole("navigation", { name: "Workspace navigation" }).getByRole("link", { name: "Triage inbox" }).click();
    const needsReview = page.getByRole("link", { name: /Document the profile format/ });
    await needsReview.press("Enter");
    const decision = detail.getByRole("form", { name: "Accept issue" });
    const nextStep = decision.getByLabel("Next step");
    await nextStep.pressSequentially("Write a profile format example.");
    await page.keyboard.press("Tab");
    await expect(decision.getByRole("button", { name: "Accept issue" })).toBeFocused();
    await page.keyboard.press("Enter");
    const saved = detail.getByText("Issue accepted.", { exact: true });
    await expect(saved).toBeFocused();
    await detail.getByRole("link", { name: "Back to needs review" }).press("Enter");
    await expect(page.getByRole("heading", { name: "Needs review · 0" })).toBeFocused();
    await expect(page.getByText("No issues need review.", { exact: false })).toBeVisible();
  } finally {
    await memberContext.close();
  }
});
