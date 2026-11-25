import type { components } from "./api-types";

export type Schema = components["schemas"];
export class ApiError extends Error {
  constructor(public readonly status: number, message: string, public readonly code?: string,
    public readonly currentTriage?: Schema["Triage"]) { super(message); }
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers);
  const method = options.method ?? "GET";
  if (method !== "GET") {
    const token = await api<Schema["Csrf"]>("/auth/csrf", { signal: options.signal });
    headers.set("X-CSRF-Token", token.requestToken);
    if (options.body) headers.set("Content-Type", "application/json");
  }

  const response = await fetch("/api/v1" + path, { ...options, headers, cache: "no-store" });
  if (!response.ok) {
    const issue = await response.json().catch(() => null) as Schema["Problem"] | null;
    throw new ApiError(response.status, issue?.title ?? "The service is temporarily unavailable.",
      issue?.code, issue?.currentTriage);
  }
  if (response.status === 204 || response.status === 202) return undefined as T;
  return response.json() as Promise<T>;
}

export function messageFor(error: unknown) {
  return error instanceof ApiError ? error.message : "Could not reach Triage Desk. Try again.";
}
