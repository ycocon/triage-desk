import { expect, test, type BrowserContext } from "@playwright/test";
import { resolve } from "node:path";
import { registerAndVerify, testEmail } from "./accounts";

test("connects a public repository, imports paginated reports and opens issue detail", async ({ page, browser, request }) => {
  await registerAndVerify(page, "Ari");
  await page.getByRole("button", { name: "New workspace" }).click();
  await page.getByLabel("Workspace name").fill("Northline Repos");
  await page.getByRole("button", { name: "Create workspace", exact: true }).click();
  await page.getByRole("link", { name: "View repositories", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Repositories", exact: true })).toBeVisible();
  const repoPath = new URL(page.url()).pathname;
  const path = repoPath.slice(0, -"/repositories".length);

  await page.getByLabel("GitHub repository").fill("northline/private");
  await page.getByRole("button", { name: "Preview repository" }).click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("could not be found");
  await page.getByLabel("GitHub repository").fill("northline/cli");
  await page.getByRole("button", { name: "Preview repository" }).click();
  await expect(page.getByText("Public repository", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Connect repository", exact: true }).click();
  await expect(page.getByText("Never refreshed successfully")).toBeVisible();

  await page.goto(path + "/team");
  const contexts: BrowserContext[] = [];
  try {
    for (const [name, role] of [["Mara", "reviewer"], ["Jules", "member"]] as const) {
      const email = testEmail(name);
      await page.getByLabel("Email address").fill(email);
      await page.getByLabel("Invitation role").selectOption(role);
      await page.getByRole("button", { name: "Create invitation" }).click();
      const link = page.getByLabel("Private invitation link");
      await expect(link).toBeVisible();
      const context = await browser.newContext({ baseURL: "https://localhost:3000", ignoreHTTPSErrors: true });
      contexts.push(context);
      const teammate = await context.newPage();
      await teammate.goto(await link.inputValue());
      await expect(teammate.getByRole("heading", { name: "Sign in" })).toBeVisible();
      await registerAndVerify(teammate, name, email, "Join Northline Repos");
      await teammate.getByRole("button", { name: "Accept invitation" }).click();
      await expect(teammate.getByRole("heading", { name: "Team" })).toBeVisible();
    }

    const reviewer = contexts[0].pages()[0];
    const member = contexts[1].pages()[0];
    await reviewer.goto(repoPath);
    await expect(reviewer.getByRole("button", { name: "Refresh issues" })).toBeVisible();
    await expect(reviewer.getByRole("heading", { name: "Connect a repository" })).toHaveCount(0);
    await reviewer.getByRole("button", { name: "Refresh issues" }).click();
    await expect(reviewer.getByText("Refresh complete.", { exact: true })).toBeVisible();
    await expect(reviewer.getByText(/Last successful refresh/)).toBeVisible();
    await expect(reviewer.getByText(/4 examined · 2 imported/)).toBeVisible();

    await member.goto(repoPath);
    const repo = await member.request.get("/api/v1" + repoPath);
    const [connection] = (await repo.json()).items;
    await expect(member.getByText("northline/cli", { exact: true })).toBeVisible();
    await expect(member.getByRole("button", { name: "Refresh issues" })).toHaveCount(0);
    const csrf = await member.request.get("/api/v1/auth/csrf");
    expect((await member.request.post("/api/v1" + repoPath + "/" + connection.repositoryId + "/sync-runs", {
      data: { runId: crypto.randomUUID() }, headers: { "X-CSRF-Token": (await csrf.json()).requestToken },
    })).status()).toBe(403);

    await reviewer.getByRole("link", { name: "Open triage inbox", exact: true }).click();
    await expect(reviewer.getByRole("heading", { name: "Needs review · 2" })).toBeVisible();
    await expect(reviewer.getByText("A pull request should stay out")).toHaveCount(0);
    await reviewer.getByRole("link", { name: /CLI crashes on Windows/ }).click();
    const detail = reviewer.getByRole("region", { name: "Issue detail" });
    await expect(detail.getByRole("heading", { name: "CLI crashes on Windows" })).toBeVisible();
    await expect(detail.getByText("northline/cli #42", { exact: true })).toBeVisible();
    await expect(detail.getByText("Needs review", { exact: true })).toBeVisible();
    await expect(detail.getByText("GitHub Open", { exact: true })).toBeVisible();
    await expect(detail.getByRole("link", { name: "View on GitHub" })).toHaveAttribute("href", "https://github.com/northline/cli/issues/42");
    if (process.env.TRIAGEDESK_IMPORT_SCREENSHOT)
      await reviewer.screenshot({ path: resolve(process.env.TRIAGEDESK_IMPORT_SCREENSHOT), fullPage: true });

    await member.goto(path + "/inbox");
    await expect(member.getByRole("heading", { name: "Needs review · 2" })).toBeVisible();
    const counters = await request.get("http://127.0.0.1:5101/counts");
    expect(await counters.json()).toMatchObject({ writes: 0 });
  } finally {
    await Promise.all(contexts.map(context => context.close()));
  }
});
