import { expect, test } from "@playwright/test";
import { resolve } from "node:path";
import { registerAndVerify } from "./accounts";

test("verified owner creates and reopens a workspace while another account is denied", async ({ page, browser }) => {
  await registerAndVerify(page, "Owner");
  const cookies = await page.context().cookies();
  expect(cookies.find(c => c.name === "__Host-td-session")).toMatchObject({
    httpOnly: true, secure: true, sameSite: "Lax", path: "/", expires: -1,
  });

  const noCsrf = await page.request.post("/api/v1/workspaces", { data: { name: "Rejected workspace" } });
  expect(noCsrf.status()).toBe(403);
  expect(await noCsrf.json()).toMatchObject({ code: "csrf_failed" });

  await page.getByRole("button", { name: "New workspace" }).click();
  await page.getByLabel("Workspace name").fill("Northline Tools");
  await page.getByRole("button", { name: "Create workspace", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Northline Tools" })).toBeVisible();
  await expect(page.getByText("Private workspace")).toContainText("owner");
  const path = new URL(page.url()).pathname;
  await page.getByLabel("Appearance").selectOption("dark");
  await expect(page.locator(".workspace-layout")).toHaveAttribute("data-theme", "dark");
  await page.reload();
  await expect(page.getByRole("heading", { name: "Northline Tools" })).toBeVisible();
  await expect(page.getByLabel("Appearance")).toHaveValue("dark");
  await page.getByRole("link", { name: "View workspaces" }).click();
  await expect(page.getByRole("link", { name: /Northline Tools/ })).toBeVisible();
  await expect(page.getByText("Rejected workspace")).toHaveCount(0);
  await page.getByRole("link", { name: /Northline Tools/ }).click();
  await expect(page).toHaveURL(path);
  if (process.env.TRIAGEDESK_SCREENSHOT) {
    await expect(page.getByRole("heading", { name: "Northline Tools" })).toBeVisible();
    await page.screenshot({ path: resolve(process.env.TRIAGEDESK_SCREENSHOT) });
  }

  const stranger = await browser.newContext({ baseURL: "https://localhost:3000", ignoreHTTPSErrors: true });
  try {
    const otherPage = await stranger.newPage();
    await registerAndVerify(otherPage, "Other");
    const list = await stranger.request.get("/api/v1/workspaces");
    expect(await list.json()).toMatchObject({ total: 0, items: [] });
    const response = await stranger.request.get("/api/v1" + path);
    expect(response.status()).toBe(404);
    expect(await response.json()).toMatchObject({ code: "not_found" });
    await otherPage.goto(path);
    await expect(otherPage.getByRole("heading", { name: "Workspace unavailable" })).toBeVisible();
    await expect(otherPage.getByText("Northline Tools")).toHaveCount(0);
  } finally {
    await stranger.close();
  }

  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  expect((await page.request.get("/api/v1" + path)).status()).toBe(401);
  await page.goto(path);
  await expect(page).toHaveURL("/sign-in");
});
