import type { operations } from "@/lib/api-types";

export const inboxViews = ["needsReview", "available", "myWork", "all", "activeWork", "sourceChanges"] as const;
export const statuses = ["needsReview", "ready", "waitingForInformation", "inProgress", "done", "setAside"] as const;
export const priorities = ["high", "normal", "low"] as const;
export const sorts = ["enteredReviewAt", "priority", "updatedAt", "number"] as const;
const states = ["open", "closed"] as const;
const directions = ["asc", "desc"] as const;
const filterKeys = ["repositoryId", "status", "githubState", "priority", "assigneeId", "q", "sort", "direction"] as const;
const id = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

type IssueFilters = NonNullable<operations["listIssues"]["parameters"]["query"]>;
export type InboxQuery = Omit<IssueFilters, "pageSize" | "view" | "page"> &
  Required<Pick<IssueFilters, "view" | "page">> & { issue?: string };

type Query = Record<string, string | string[] | null | undefined>;

function memberOf<T extends string>(list: readonly T[], value: Query[string]) {
  return list.find(item => item === value);
}

export function localId(value: Query[string]) {
  return typeof value === "string" && id.test(value) ? value.toLowerCase() : undefined;
}

export function pageNumber(value: Query[string]) {
  const number = typeof value === "string" && /^[0-9]+$/.test(value) ? Number(value) : 1;
  return Number.isSafeInteger(number) && number >= 1 && number <= 2147483647 ? number : 1;
}

export function readInboxQuery(values: Query): InboxQuery {
  return {
    view: memberOf(inboxViews, values.view) ?? "needsReview",
    page: pageNumber(values.page),
    issue: localId(values.issue),
    repositoryId: localId(values.repositoryId),
    status: memberOf(statuses, values.status),
    githubState: memberOf(states, values.githubState),
    priority: memberOf(priorities, values.priority),
    assigneeId: values.assigneeId === "unassigned" ? values.assigneeId : localId(values.assigneeId),
    q: typeof values.q === "string" ? values.q.trim().slice(0, 160) || undefined : undefined,
    sort: memberOf(sorts, values.sort),
    direction: memberOf(directions, values.direction),
  };
}

export function inboxParams(query: InboxQuery, includeIssue = false) {
  const params = new URLSearchParams({ view: query.view, page: String(query.page) });
  for (const name of filterKeys) {
    const value = query[name];
    if (value) params.set(name, value);
  }
  if (includeIssue && query.issue) params.set("issue", query.issue);
  return params.toString();
}

export function inboxHref(path: string, query: InboxQuery) {
  return path + "/inbox?" + inboxParams(query, true);
}
