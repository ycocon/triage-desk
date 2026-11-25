import { expect, test } from "@playwright/test";
import { resolve } from "node:path";
import { importedWorkspace, postDecision, teammate } from "./triage-fixture";

test.use({ actionTimeout: 20_000, navigationTimeout: 30_000 });

test("asked question returns to review with an answer and a stale edit keeps the draft", async ({ page, browser }) => {
  const name = "Northline Questions";
  const path = await importedWorkspace(page, name);
  const inbox = path + "/inbox";
  const options = { baseURL: "https://localhost:3000", ignoreHTTPSErrors: true };
  const reviewerSession = await browser.newContext(options);
  const memberSession = await browser.newContext(options);
  const question = "Which Windows version shows the crash?";
  const answer = "Windows 11 on a clean install.";
  const draft = "Collect the setup log from Windows 11.";
  try {
    const reviewer = await teammate(page, reviewerSession, path, name, "Mara", "reviewer");
    const member = await teammate(page, memberSession, path, name, "Jules", "member");
    await reviewer.goto(inbox);
    await reviewer.getByRole("link", { name: /CLI crashes on Windows/ }).click();
    const detail = reviewer.getByRole("region", { name: "Issue detail" });
    await detail.getByRole("button", { name: "Request information" }).click();
    await detail.getByLabel("Assignee").selectOption({ label: "Jules" });
    await detail.getByLabel("Question", { exact: true }).fill(question);
    await detail.getByRole("button", { name: "Send question" }).click();
    await expect(detail.getByText("Waiting for information", { exact: true })).toBeVisible();
    const issueId = new URL(reviewer.url()).searchParams.get("issue");
    expect(issueId).toBeTruthy();
    const issue = path + "/issues/" + issueId;

    await member.goto(inbox + "?view=myWork");
    await member.getByRole("link", { name: /CLI crashes on Windows/ }).click();
    const memberDetail = member.getByRole("region", { name: "Issue detail" });
    await expect(memberDetail.getByText(question, { exact: true }).first()).toBeVisible();
    await memberDetail.getByLabel("Answer", { exact: true }).fill(answer);
    await memberDetail.getByRole("button", { name: "Submit answer" }).click();
    await expect(member.getByText("No active assignments.", { exact: true })).toBeVisible();

    await reviewer.reload();
    await expect(detail.getByText("Needs review", { exact: true })).toBeVisible();
    const history = detail.getByRole("region", { name: "Issue history" });
    const answered = history.getByRole("listitem").filter({ hasText: "answered the question" });
    await expect(answered).toContainText(question);
    await expect(answered).toContainText(answer);
    await detail.getByLabel("Next step", { exact: true }).fill("Reproduce on a clean install.");
    await detail.getByRole("button", { name: "Accept issue" }).click();
    await expect(detail.getByText("Ready", { exact: true })).toBeVisible();

    await detail.getByRole("button", { name: "Edit next step" }).click();
    await detail.getByLabel("Next step", { exact: true }).fill(draft);
    const response = await page.request.get("/api/v1" + issue);
    const saved = (await response.json()).triage;
    await postDecision(page.request, issue + "/commands", {
      kind: "changePriority", commandId: crypto.randomUUID(), expectedVersion: saved.decisionVersion, priority: "high",
    }, 200);
    await detail.getByRole("button", { name: "Save next step" }).click();
    await expect(detail.getByRole("alert")).toContainText("The latest decision is shown above.");
    await expect(detail.getByText("high priority", { exact: true })).toBeVisible();
    await expect(detail.getByLabel("Next step", { exact: true })).toHaveValue(draft);
    await expect(detail.getByRole("paragraph").filter({ hasText: "Reproduce on a clean install." })).toBeVisible();

    if (process.env.TRIAGEDESK_REVIEW_SCREENSHOT)
      await reviewer.screenshot({ path: resolve(process.env.TRIAGEDESK_REVIEW_SCREENSHOT), fullPage: true });

    await detail.getByRole("button", { name: "Save next step" }).click();
    await expect(detail.getByText("Next step saved.", { exact: true })).toBeVisible();
    await expect(detail.getByText(draft, { exact: true })).toBeVisible();
    await expect(history.getByText(/edited the next step/)).toBeVisible();
  } finally {
    await Promise.all([reviewerSession.close(), memberSession.close()]);
  }
});
