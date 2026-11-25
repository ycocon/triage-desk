import { readFileSync } from "node:fs";
import { createServer } from "node:https";
import { resolve } from "node:path";
import next from "next";

const hostname = "127.0.0.1";
const port = 3000;
const certificates = resolve("../../.local");
const app = next({ dev: false, hostname, port, dir: process.cwd() });
await app.prepare();
const handle = app.getRequestHandler();
createServer({
  key: readFileSync(resolve(certificates, "localhost.key")),
  cert: readFileSync(resolve(certificates, "localhost.pem")),
}, handle).listen(port, hostname);
