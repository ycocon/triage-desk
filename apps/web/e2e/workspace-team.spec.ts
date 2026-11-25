import { expect, test, type Page } from "@playwright/test";
import { resolve } from "node:path";
import { registerAndVerify, testEmail } from "./accounts";

async function invite(page: Page, email: string, role: "member" | "reviewer") {
  await page.getByLabel("Email address").fill(email);
  await page.getByLabel("Invitation role").selectOption(role);
  await page.getByRole("button", { name: "Create invitation" }).click();
  const link = page.getByLabel("Private invitation link");
  await expect(link).toBeVisible();
  const url = new URL(await link.inputValue());
  expect(url.search).toBe("");
  expect(url.hash).toMatch(/^#token=[A-Za-z0-9_-]{43}$/);
  return url.toString();
}

async function join(page: Page, url: string, name: string, email: string, role: string) {
  await page.goto(url);
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await registerAndVerify(page, name, email, "Join Northline Access");
  expect(new URL(page.url()).hash).toBe("");
  await expect(page.getByText("You’ve been invited as")).toContainText(role);
  await page.getByRole("button", { name: "Accept invitation" }).click();
  await expect(page.getByRole("heading", { name: "Team" })).toBeVisible();
  await expect(page.getByRole("listitem").filter({ hasText: name })).toContainText(role);
  await expect(page.getByRole("heading", { name: "Invite a teammate" })).toHaveCount(0);
}

test("Owner invites a Reviewer and a Member, removes access, and transfers ownership", async ({ page, browser }) => {
  await registerAndVerify(page, "Ari");
  await page.getByRole("button", { name: "New workspace" }).click();
  await page.getByLabel("Workspace name").fill("Northline Access");
  await page.getByRole("button", { name: "Create workspace", exact: true }).click();
  await page.getByRole("link", { name: "View team" }).click();
  await expect(page.getByRole("heading", { name: "Invite a teammate" })).toBeVisible();
  const teamPath = new URL(page.url()).pathname;
  const workspacePath = teamPath.replace(/\/team$/, "");
  const maraEmail = testEmail("Mara");
  const julesEmail = testEmail("Jules");
  const reviewerLink = await invite(page, maraEmail, "reviewer");
  const memberLink = await invite(page, julesEmail, "member");

  const sessionOptions = { baseURL: "https://localhost:3000", ignoreHTTPSErrors: true };
  const reviewer = await browser.newContext(sessionOptions);
  const member = await browser.newContext(sessionOptions);
  try {
    const mara = await reviewer.newPage();
    const jules = await member.newPage();
    await join(mara, reviewerLink, "Mara", maraEmail, "reviewer");
    await join(jules, memberLink, "Jules", julesEmail, "member");

    await page.reload();
    const roster = page.getByRole("region", { name: "Members" });
    await expect(roster.getByRole("listitem")).toHaveCount(3);
    if (process.env.TRIAGEDESK_TEAM_SCREENSHOT) {
      await expect(page.getByLabel("Private invitation link")).toHaveCount(0);
      await page.screenshot({ path: resolve(process.env.TRIAGEDESK_TEAM_SCREENSHOT), fullPage: true });
    }

    const julesRow = roster.getByRole("listitem").filter({ hasText: "Jules" });
    await julesRow.getByLabel("Role for Jules").selectOption("reviewer");
    await expect(julesRow.getByLabel("Role for Jules")).toHaveValue("reviewer");
    await jules.reload();
    await expect(jules.getByRole("listitem").filter({ hasText: "Jules" })).toContainText("reviewer");

    await julesRow.getByRole("button", { name: "Remove", exact: true }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Remove teammate" }).click();
    await expect(roster.getByText("Jules")).toHaveCount(0);
    expect((await jules.request.get("/api/v1" + workspacePath + "/members")).status()).toBe(404);
    expect((await jules.request.get("/api/v1/me")).status()).toBe(200);
    await jules.reload();
    await expect(jules.getByRole("heading", { name: "Workspace unavailable" })).toBeVisible();

    // Leave the old page alive while ownership has changed on the server.
    await page.route("**" + teamPath, async route => {
      await new Promise(resolve => setTimeout(resolve, 500));
      await route.continue();
    });
    await roster.getByRole("listitem").filter({ hasText: "Mara" }).getByRole("button", { name: "Transfer ownership" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Confirm transfer" }).click();
    await expect(page.getByRole("heading", { name: "Invite a teammate" })).toHaveCount(0);
    await expect(roster.getByRole("listitem").filter({ hasText: "Ari" })).toContainText("member");
    expect(new URL(page.url()).pathname).toBe(teamPath);
    await mara.reload();
    await expect(mara.getByRole("heading", { name: "Invite a teammate" })).toBeVisible();
    await expect(mara.getByRole("region", { name: "Members" }).getByText("owner · You")).toBeVisible();

    await page.getByRole("button", { name: "Leave workspace" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Leave workspace" }).click();
    await expect(page.getByRole("heading", { name: "Your workspaces" })).toBeVisible();
    expect((await page.request.get("/api/v1" + workspacePath)).status()).toBe(404);
    await mara.reload();
    await expect(mara.getByRole("region", { name: "Members" }).getByRole("listitem")).toHaveCount(1);
  } finally {
    await reviewer.close();
    await member.close();
  }
});
