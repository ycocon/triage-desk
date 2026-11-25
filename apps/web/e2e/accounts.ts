import { expect, type Page } from "@playwright/test";
import { createHash, randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

const password = "local test password 2026";

export function testEmail(name: string) {
  return name.toLowerCase() + "-" + randomUUID().slice(0, 12) + "@example.test";
}

export async function registerAndVerify(page: Page, name: string,
  email = testEmail(name), destination = "Your workspaces") {
  await page.goto("/register");
  await page.getByLabel("Display name").fill(name);
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByRole("heading", { name: "Verify your email" })).toBeVisible();

  const key = createHash("sha256").update(email.toUpperCase()).digest("hex");
  const message = JSON.parse(await readFile(resolve("../../.local/inbox", key + ".json"), "utf-8"));
  expect(message.to).toBe(email);

  await page.goto("/sign-in");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("verify your email");

  await page.goto(message.url);
  await page.getByRole("button", { name: "Confirm email", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Email verified" })).toBeVisible();
  expect(new URL(page.url()).hash).toBe("");
  await page.getByRole("link", { name: "Continue to sign in" }).click();
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("heading", { name: destination })).toBeVisible();
}
