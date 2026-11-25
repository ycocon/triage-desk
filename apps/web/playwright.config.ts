import { defineConfig, devices } from "@playwright/test";
import { resolve } from "node:path";

const root = resolve(__dirname, "../..");
const local = resolve(root, ".local");
const address = "https://localhost:3000";
const api = "https://127.0.0.1:5100";
const apiConfiguration = process.env.TRIAGEDESK_RELEASE_TEST ? "Release" : "Debug";
const browsers = [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }];
const releaseBrowsers = [...browsers,
  { name: "firefox", use: { ...devices["Desktop Firefox"] } },
  { name: "webkit", use: { ...devices["Desktop Safari"] } }];

export default defineConfig({
  testDir: "./e2e",
  timeout: 120_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  workers: 1,
  retries: 0,
  use: { baseURL: address, ignoreHTTPSErrors: true },
  projects: process.env.TRIAGEDESK_RELEASE_TEST ? releaseBrowsers : browsers,
  webServer: process.env.TRIAGEDESK_EXTERNAL_SERVERS ? undefined : [
    {
      name: "GitHub fixture",
      command: "node e2e/github-fixture.mjs",
      url: "http://127.0.0.1:5101/counts",
    },
    {
      name: "API",
      command: "dotnet run --no-build --no-launch-profile --configuration " + apiConfiguration + " --project apps/api/TriageDesk.Api",
      cwd: root,
      url: api + "/health/ready",
      ignoreHTTPSErrors: true,
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        ASPNETCORE_URLS: api,
        Kestrel__Certificates__Default__Path: resolve(local, "localhost.pem"),
        Kestrel__Certificates__Default__KeyPath: resolve(local, "localhost.key"),
        ConnectionStrings__TriageDesk: process.env.ConnectionStrings__TriageDesk ??
          "Host=127.0.0.1;Port=55432;Database=triage_desk;Username=postgres;Password=local-triage-desk",
        Identity__InboxDirectory: resolve(local, "inbox"),
        Identity__KeyDirectory: resolve(local, "keys"),
        Identity__WebOrigin: address,
        GitHub__FixtureOrigin: "http://127.0.0.1:5101",
      },
    },
    {
      name: "Web",
      command: process.env.TRIAGEDESK_RELEASE_TEST ? "node e2e/serve-built.mjs" :
        "npm run dev -- --hostname 127.0.0.1 --experimental-https --experimental-https-cert ../../.local/localhost.pem --experimental-https-key ../../.local/localhost.key --experimental-https-ca ../../.local/localhost-ca.pem",
      url: address + "/sign-in",
      timeout: 120_000,
      ignoreHTTPSErrors: true,
      env: { TRIAGEDESK_API_ORIGIN: api, NEXT_TELEMETRY_DISABLED: "1",
        NODE_EXTRA_CA_CERTS: resolve(local, "localhost-ca.pem") },
    },
  ],
});
