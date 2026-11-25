import { createServer } from "node:http";

const address = "127.0.0.1";
let reads = 0;
let writes = 0;
const issue = (id, number, title, body, labels = []) => ({
  id, number, title, body, state: "open", labels: labels.map(name => ({ name })),
  created_at: "2026-10-11T09:00:00Z", updated_at: "2026-10-12T09:00:00Z",
});
const first = issue(201, 42, "CLI crashes on Windows", "Running the CLI without a config file exits early.", ["bug", "windows"]);
const second = issue(203, 44, "Clarify the setup instructions", "Add Windows setup steps.");
const pullRequest = { ...issue(202, 43, "A pull request should stay out", null), pull_request: {} };

createServer((req, res) => {
  const url = new URL(req.url, "http://" + address);
  function send(status, body, headers = {}) {
    res.writeHead(status, { "content-type": "application/json", ...headers });
    res.end(JSON.stringify(body));
  }
  if (url.pathname === "/counts") return send(200, { reads, writes });
  if (req.method !== "GET" || req.headers.authorization) {
    writes++;
    return send(405, { message: "The fixture accepts only public reads." });
  }
  reads++;
  if (url.pathname === "/repos/northline/cli") return send(200, { id: 801, full_name: "northline/cli", private: false });
  if (url.pathname === "/repos/northline/private") return send(200, { id: 802, full_name: "northline/private", private: true });
  if (url.pathname !== "/repos/northline/cli/issues" || url.searchParams.get("state") !== "open")
    return send(404, { message: "Not found" });
  if (url.searchParams.get("page") === "1") return send(200, [first, pullRequest], {
    Link: '<https://api.github.com/repositories/801/issues?page=2>; rel="next"',
  });
  if (url.searchParams.get("page") === "2") return send(200, [first, second]);
  return send(404, { message: "Unexpected page" });
}).listen(5101, address);
