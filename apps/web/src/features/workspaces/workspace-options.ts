import { api, type Schema } from "@/lib/http";

async function allPages<T>(path: string, signal: AbortSignal) {
  const items: T[] = [];
  for (let page = 1; ; page++) {
    const result = await api<{ items: T[]; pageSize: number; total: number }>(
      path + "?pageSize=100&page=" + page, { signal });
    items.push(...result.items);
    if (page * result.pageSize >= result.total || !result.items.length) return items;
  }
}

export function activeMembers(workspacePath: string, signal: AbortSignal) {
  return allPages<Schema["Member"]>(workspacePath + "/members", signal);
}

export function connectedRepositories(workspacePath: string, signal: AbortSignal) {
  return allPages<Schema["Repository"]>(workspacePath + "/repositories", signal);
}
