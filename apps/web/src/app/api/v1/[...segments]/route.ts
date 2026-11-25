import { NextRequest } from "next/server";

const routes: Record<string, string[]> = {
  "auth/csrf": ["GET"],
  "auth/register": ["POST"],
  "auth/email-verification/requests": ["POST"],
  "auth/email-verification": ["POST"],
  "auth/login": ["POST"],
  "auth/logout": ["POST"],
  "me": ["GET"],
  "me/preferences": ["PATCH"],
  "invitations/preview": ["POST"],
  "invitations/accept": ["POST"],
  "workspaces": ["GET", "POST"],
};
const id = "[a-f\\d]{8}(?:-[a-f\\d]{4}){3}-[a-f\\d]{12}";
const workspaceRoutes = [
  [new RegExp(`^workspaces/${id}$`, "i"), ["GET"]],
  [new RegExp(`^workspaces/${id}/members$`, "i"), ["GET"]],
  [new RegExp(`^workspaces/${id}/members/${id}$`, "i"), ["PATCH", "DELETE"]],
  [new RegExp(`^workspaces/${id}/membership$`, "i"), ["DELETE"]],
  [new RegExp(`^workspaces/${id}/ownership$`, "i"), ["PUT"]],
  [new RegExp(`^workspaces/${id}/invitations$`, "i"), ["GET", "POST"]],
  [new RegExp(`^workspaces/${id}/invitations/${id}$`, "i"), ["DELETE"]],
  [new RegExp(`^workspaces/${id}/repository-previews$`, "i"), ["POST"]],
  [new RegExp(`^workspaces/${id}/repositories$`, "i"), ["GET", "POST"]],
  [new RegExp(`^workspaces/${id}/repositories/${id}$`, "i"), ["GET", "DELETE"]],
  [new RegExp(`^workspaces/${id}/repositories/${id}/sync-runs$`, "i"), ["POST"]],
  [new RegExp(`^workspaces/${id}/repositories/${id}/sync-runs/${id}$`, "i"), ["GET"]],
  [new RegExp(`^workspaces/${id}/repositories/${id}/sync-runs/${id}/(advance|pause)$`, "i"), ["POST"]],
  [new RegExp(`^workspaces/${id}/issues(?:/${id})?$`, "i"), ["GET"]],
  [new RegExp(`^workspaces/${id}/issues/${id}/(?:claim|commands)$`, "i"), ["POST"]],
  [new RegExp(`^workspaces/${id}/issues/${id}/activity$`, "i"), ["GET"]],
  [new RegExp(`^workspaces/${id}/(?:activity|reports/(?:overview|workload))$`, "i"), ["GET"]],
] as const;
const cookies = new Set(["__Host-td-session", "__Host-td-csrf"]);
const relayHeaders = ["content-type", "location", "retry-after"];
const maxBody = 16_384;

function problem(status: number, code: string, title: string, traceId = crypto.randomUUID()) {
  return Response.json({ type: "urn:triagedesk:problem:" + code, status, code, title,
    traceId }, { status, headers: {
    "content-type": "application/problem+json",
    "cache-control": "private, no-store",
  } });
}

async function relay(request: NextRequest, context: { params: Promise<{ segments: string[] }> }) {
  const path = (await context.params).segments.join("/");
  const methods: readonly string[] = Object.hasOwn(routes, path)
    ? routes[path] : workspaceRoutes.find(([pattern]) => pattern.test(path))?.[1] ?? [];
  if (!methods.includes(request.method))
    return problem(404, "not_found", "This resource could not be found.");

  try {
    const origin = new URL(process.env.TRIAGEDESK_API_ORIGIN ?? "https://127.0.0.1:5100");
    if (origin.protocol !== "https:" ||
        origin.username || origin.password || origin.pathname !== "/" || origin.search || origin.hash) {
      throw new Error("Invalid backend origin");
    }

    const target = new URL("/api/v1/" + path, origin);
    target.search = request.nextUrl.search;
    const headers = new Headers({ Accept: "application/json, application/problem+json" });
    const cookie = request.cookies.getAll().filter(item => cookies.has(item.name))
      .map(item => item.name + "=" + encodeURIComponent(item.value)).join("; ");
    if (cookie) headers.set("Cookie", cookie);
    for (const name of ["content-type", "x-csrf-token"]) {
      const value = request.headers.get(name);
      if (value) headers.set(name, value);
    }

    if (Number(request.headers.get("content-length")) > maxBody)
      return problem(413, "request_too_large", "The request is too large.");

    let body: Uint8Array<ArrayBuffer> | undefined;
    if (request.body) {
      const reader = request.body.getReader();
      body = new Uint8Array(maxBody);
      let length = 0;
      for (;;) {
        const chunk = await reader.read();
        if (chunk.done) break;
        if (length + chunk.value.length > maxBody) {
          await reader.cancel();
          return problem(413, "request_too_large", "The request is too large.");
        }
        body.set(chunk.value, length);
        length += chunk.value.length;
      }
      body = body.subarray(0, length);
    }

    // A browser disconnect can occur after a command reaches the API.
    // Give the upstream request its own deadline and let the API finish its transaction.
    const response = await fetch(target, { method: request.method, headers, body,
      cache: "no-store", redirect: "manual", signal: AbortSignal.timeout(15000) });
    const output = new Headers({ "cache-control": "private, no-store" });
    for (const name of relayHeaders) {
      const value = response.headers.get(name);
      if (value) output.set(name, value);
    }
    for (const cookie of response.headers.getSetCookie()) {
      if (cookies.has(cookie.split("=", 1)[0])) output.append("set-cookie", cookie);
    }
    return new Response(response.body, { status: response.status, headers: output });
  } catch (error) {
    const traceId = crypto.randomUUID();
    const cause = error instanceof Error ? error.cause as NodeJS.ErrnoException | undefined : undefined;
    console.error("Triage Desk API request failed", { traceId, kind: cause?.code ?? (error instanceof Error ? error.name : "Unknown") });
    return problem(503, "service_unavailable", "Triage Desk is temporarily unavailable.", traceId);
  }
}

export const GET = relay;
export const POST = relay;
export const PATCH = relay;
export const PUT = relay;
export const DELETE = relay;
