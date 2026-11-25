import { expect, test } from "@playwright/test";
import { resolve } from "node:path";
import { importedWorkspace, teammate } from "./triage-fixture";

test.use({ actionTimeout: 20_000, navigationTimeout: 30_000 });

test("Reviewer sees a Member complete a self-claimed issue while GitHub stays Open", async ({ page, browser, request }) => {
  const path = await importedWorkspace(page, "Northline Handoff");
  const inbox = path + "/inbox";
  const countUrl = "http://127.0.0.1:5101/counts";
  const before = await (await request.get(countUrl)).json();

  const options = { baseURL: "https://localhost:3000", ignoreHTTPSErrors: true };
  const reviewerSession = await browser.newContext(options);
  const memberSession = await browser.newContext(options);
  try {
    const reviewer = await teammate(page, reviewerSession, path, "Northline Handoff", "Mara", "reviewer");
    const member = await teammate(page, memberSession, path, "Northline Handoff", "Jules", "member");

    await reviewer.goto(inbox);
    await reviewer.getByRole("link", { name: /CLI crashes on Windows/ }).click();
    const reviewerDetail = reviewer.getByRole("region", { name: "Issue detail" });
    await reviewerDetail.getByLabel("Priority").selectOption("high");
    await reviewerDetail.getByLabel("Next step").fill("Reproduce the crash on Windows with no config file.");
    await reviewerDetail.getByRole("button", { name: "Accept issue" }).click();
    await expect(reviewerDetail.getByText("Issue accepted.", { exact: true })).toBeVisible();
    await expect(reviewer.getByRole("heading", { name: "Needs review · 1" })).toBeVisible();

    await member.goto(inbox);
    await member.getByRole("link", { name: "Available to claim", exact: true }).click();
    await member.getByRole("link", { name: /CLI crashes on Windows/ }).click();
    const memberDetail = member.getByRole("region", { name: "Issue detail" });
    await expect(memberDetail.getByText("Reproduce the crash on Windows with no config file.", { exact: true })).toBeVisible();
    await memberDetail.getByRole("button", { name: "Claim issue" }).click();
    await expect(memberDetail.getByText("Ready", { exact: true })).toBeVisible();
    await expect(member.getByRole("heading", { name: "Available to claim · 0" })).toBeVisible();
    await memberDetail.getByRole("link", { name: "Open My work" }).click();
    await expect(member.getByRole("heading", { name: "My work · 1" })).toBeVisible();
    await member.getByRole("link", { name: /CLI crashes on Windows/ }).click();
    const start = memberDetail.getByRole("button", { name: "Start work" });
    await expect(start).toBeVisible();
    await start.click();
    await expect(memberDetail.getByText("In progress", { exact: true })).toBeVisible();
    await memberDetail.getByRole("button", { name: "Mark Done" }).click();
    await expect(memberDetail.getByText("Done", { exact: true })).toBeVisible();
    await expect(member.getByText("No active assignments.", { exact: true })).toBeVisible();

    await reviewer.reload();
    await reviewer.getByRole("link", { name: "All issues", exact: true }).click();
    await reviewer.getByRole("link", { name: "Done", exact: true }).click();
    await expect(reviewer.getByRole("heading", { name: "Done · 1" })).toBeVisible();
    await reviewer.getByRole("link", { name: /CLI crashes on Windows/ }).click();
    await expect(reviewerDetail.getByText("Done", { exact: true })).toBeVisible();
    await expect(reviewerDetail.getByText("GitHub Open", { exact: true })).toBeVisible();
    await expect(reviewerDetail.getByText(/Assigned to/)).toContainText("Jules");
    const history = reviewerDetail.getByRole("region", { name: "Issue history" });
    const completion = history.getByRole("listitem").filter({ hasText: "marked the issue Done" });
    await expect(completion).toContainText("Jules");
    await expect(completion.locator("time")).toHaveAttribute("datetime", /^\d{4}-\d{2}-\d{2}T/);
    await expect(history.getByRole("listitem")).toHaveCount(4);
    const after = await (await request.get(countUrl)).json();
    expect(after).toEqual(before);
    expect(after.writes).toBe(0);
    if (process.env.TRIAGEDESK_HANDOFF_SCREENSHOT)
      await reviewer.screenshot({ path: resolve(process.env.TRIAGEDESK_HANDOFF_SCREENSHOT), fullPage: true });
  } finally {
    await Promise.all([reviewerSession.close(), memberSession.close()]);
  }
});
