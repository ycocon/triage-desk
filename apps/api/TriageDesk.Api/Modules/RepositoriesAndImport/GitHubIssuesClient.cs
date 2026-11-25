using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using TriageDesk.Api.Http;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal sealed record GitHubRepository(long Id, string FullName)
{
    public string WebUrl => "https://github.com/" + FullName;
}
internal sealed record GitHubIssue(long Id, int Number, string Title, string? Body, string State, string[] Labels,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
internal sealed record GitHubPage(IReadOnlyList<GitHubIssue> Issues, int Examined, int? NextPage);
internal sealed class GitHubFailure(int status, string code, TimeSpan? delay = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public TimeSpan? Delay { get; } = delay;

    internal IResult Result(HttpContext context)
    {
        Dictionary<string, object?>? extras = null;
        if (Delay is { } value)
        {
            context.Response.Headers.RetryAfter = Math.Ceiling(value.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            extras = new() { ["retryAt"] = now + value };
        }
        var title = Status switch
        {
            404 => "This public repository could not be found.",
            429 => "GitHub is limiting requests. Try again later.",
            _ => "GitHub did not return a usable response. Try again."
        };
        return ApiProblem.Result(context, Status, Code, title, extras);
    }
}

internal sealed record GitHubCanonicalIssue(GitHubRepository Repository, GitHubIssue Issue);

internal sealed partial class GitHubIssuesClient(HttpClient http, GitHubReadBudget budget)
{
    private static readonly Uri Origin = new("https://api.github.com");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();
    [GeneratedRegex("<([^>]+)>[ ]*;[ ]*rel=\"next\"", RegexOptions.CultureInvariant)]
    private static partial Regex NextLink();

    internal static bool ValidName(string? slug) =>
        slug is { Length: >= 3 and <= 140 } && RepositoryName().IsMatch(slug)
        && slug[(slug.IndexOf('/') + 1)..] is not ("." or "..")
        && !slug.Any(char.IsWhiteSpace);

    internal Task<GitHubRepository> GetRepository(string slug, CancellationToken ct) =>
        ReadRepository(new Uri(Origin, "/repos/" + slug), ct);

    internal async Task<GitHubRepository> VerifyRepository(string slug, long expectedId, CancellationToken ct)
    {
        try
        {
            var named = await GetRepository(slug, ct);
            if (named.Id == expectedId) return named;
        }
        catch (GitHubFailure failure) when (failure.Status == 404) { }
        var byId = await ReadRepository(new Uri(Origin, "/repositories/" + expectedId), ct);
        return byId.Id == expectedId ? byId : throw Upstream();
    }

    private async Task<GitHubRepository> ReadRepository(Uri address, CancellationToken ct)
    {
        using var response = await Get(address, MetadataPath, confirmMissing: true, ct);
        using var json = await Parse(response, ct);
        try
        {
            var root = json.RootElement;
            var name = root.GetProperty("full_name").GetString();
            var id = root.GetProperty("id").GetInt64();
            if (root.GetProperty("private").GetBoolean() ||
                (root.TryGetProperty("visibility", out var visibility) && visibility.GetString() != "public"))
                throw Missing();
            if (id <= 0 || !ValidName(name)) throw Upstream();
            return new GitHubRepository(id, name!);
        }
        catch (Exception ex) when (InvalidShape(ex)) { throw Upstream(); }
    }

    internal async Task<GitHubPage> GetPage(GitHubRepository repository, int page, int pageSize,
        DateTimeOffset? since, CancellationToken ct)
    {
        var path = $"/repos/{repository.FullName}/issues?" +
            (since is null ? "state=open&sort=created" : "state=all&sort=updated&since=" +
                Uri.EscapeDataString(since.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))) +
            $"&direction=asc&per_page={pageSize}&page={page}";
        using var response = await Get(new Uri(Origin, path), redirectPath: null, confirmMissing: false, ct);
        using var json = await Parse(response, ct);
        try
        {
            var root = json.RootElement;
            var issues = new List<GitHubIssue>();
            if (root.GetArrayLength() > pageSize) throw Upstream();
            foreach (var issue in root.EnumerateArray())
            {
                // GitHub returns pull requests from this endpoint as issue-shaped records.
                if (issue.TryGetProperty("pull_request", out _)) continue;
                var report = ReadIssue(issue);
                if (since is not null || report.State == "open") issues.Add(report);
            }
            return new GitHubPage(issues, root.GetArrayLength(), ParseNext(response, repository, page));
        }
        catch (Exception ex) when (InvalidShape(ex)) { throw Upstream(); }
    }

    internal async Task<GitHubCanonicalIssue> Recheck(GitHubRepository repository, int number, CancellationToken ct)
    {
        using var response = await Get(new Uri(Origin, $"/repos/{repository.FullName}/issues/{number}"),
            IssuePath, confirmMissing: true, ct);
        using var json = await Parse(response, ct);
        try
        {
            var root = json.RootElement;
            if (root.TryGetProperty("pull_request", out _)) throw Missing();
            var issue = ReadIssue(root);
            if (!Uri.TryCreate(root.GetProperty("repository_url").GetString(), UriKind.Absolute, out var address) ||
                !Trusted(address) || address.Query.Length != 0 || !MetadataPath(address.AbsolutePath))
                throw Upstream();

            var sameRepository = address.AbsolutePath.Equals("/repos/" + repository.FullName, StringComparison.OrdinalIgnoreCase) ||
                address.AbsolutePath == "/repositories/" + repository.Id;
            var verified = sameRepository ? repository : await ReadRepository(address, ct);
            return new GitHubCanonicalIssue(verified, issue);
        }
        catch (Exception ex) when (InvalidShape(ex)) { throw Upstream(); }
    }

    private static GitHubIssue ReadIssue(JsonElement issue)
    {
        var id = issue.GetProperty("id").GetInt64();
        var number = issue.GetProperty("number").GetInt32();
        var title = issue.GetProperty("title").GetString();
        var state = issue.GetProperty("state").GetString();
        if (id <= 0 || number <= 0 || title is null || state is not ("open" or "closed")) throw Upstream();
        var labels = issue.GetProperty("labels").EnumerateArray()
            .Select(label => label.GetProperty("name").GetString() ?? throw Upstream()).ToArray();
        return new GitHubIssue(id, number, title, issue.GetProperty("body").GetString(), state, labels,
            issue.GetProperty("created_at").GetDateTimeOffset().ToUniversalTime(),
            issue.GetProperty("updated_at").GetDateTimeOffset().ToUniversalTime());
    }

    private static int? ParseNext(HttpResponseMessage response, GitHubRepository repository, int page)
    {
        if (!response.Headers.TryGetValues("Link", out var links)) return null;
        var match = NextLink().Match(string.Join(", ", links));
        if (!match.Success) return null;

        if (!Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var next) || !Trusted(next) ||
            (!next.AbsolutePath.Equals($"/repos/{repository.FullName}/issues", StringComparison.OrdinalIgnoreCase)
             && next.AbsolutePath != $"/repositories/{repository.Id}/issues"))
            throw Upstream();

        var query = QueryHelpers.ParseQuery(next.Query);
        if (!query.TryGetValue("page", out var raw) || raw.Count != 1 || !int.TryParse(raw[0], out var following) ||
            following != page + 1 || following > 1_000_000)
            throw Upstream();
        return following;
    }

    private async Task<HttpResponseMessage> Get(Uri uri, Func<string, bool>? redirectPath, bool confirmMissing,
        CancellationToken ct)
    {
        try
        {
            for (var redirects = 0; redirects <= 2; redirects++)
            {
                budget.Reserve();
                var response = await http.GetAsync(uri, ct);
                budget.Observe(response.Headers);
                if (response.IsSuccessStatusCode) return response;
                using (response)
                {
                    if (redirectPath is not null && (int)response.StatusCode is 301 or 302 or 307 or 308 &&
                        response.Headers.Location is { } location)
                    {
                        var destination = new Uri(uri, location);
                        if (!Trusted(destination) || destination.Query.Length > 0 || !redirectPath(destination.AbsolutePath))
                            throw Upstream();
                        uri = destination;
                        continue;
                    }

                    var primary = GitHubReadBudget.TryRemaining(response.Headers, out var remaining) && remaining == 0;
                    if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                        response.StatusCode == HttpStatusCode.Forbidden && (primary || response.Headers.RetryAfter is not null ||
                        (await response.Content.ReadAsStringAsync(ct)).Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)))
                    {
                        var delay = response.Headers.RetryAfter?.Delta;
                        if (delay is null && response.Headers.RetryAfter?.Date is { } retryOn)
                            delay = retryOn - budget.Now;
                        if (primary && GitHubReadBudget.Reset(response.Headers) is { } reset)
                        {
                            var untilReset = reset - budget.Now;
                            if (delay is null || untilReset > delay.Value) delay = untilReset;
                        }
                        var wait = TimeSpan.FromSeconds(Math.Clamp(delay?.TotalSeconds ?? 60, 1, 86400));
                        budget.Block(wait);
                        throw GitHubReadBudget.Limited(wait);
                    }
                    if (confirmMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) throw Missing();
                    throw Upstream();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Upstream(); }
        catch (HttpRequestException) { throw Upstream(); }
        throw Upstream();
    }

    private static bool MetadataPath(string path) =>
        (path.StartsWith("/repos/", StringComparison.Ordinal) && ValidName(path["/repos/".Length..])) ||
        (path.StartsWith("/repositories/", StringComparison.Ordinal) &&
            long.TryParse(path["/repositories/".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0);

    private static bool IssuePath(string path)
    {
        var separator = path.LastIndexOf("/issues/", StringComparison.Ordinal);
        return separator > 0 && MetadataPath(path[..separator]) &&
            int.TryParse(path[(separator + "/issues/".Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number > 0;
    }

    private static bool Trusted(Uri uri) => uri.Scheme == "https" && uri.Host == Origin.Host &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static async Task<JsonDocument> Parse(HttpResponseMessage response, CancellationToken ct)
    {
        try { return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct)); }
        catch (JsonException) { throw Upstream(); }
    }

    private static bool InvalidShape(Exception ex) => ex is InvalidOperationException or KeyNotFoundException or FormatException;
    internal static GitHubFailure Missing() => new(404, "not_found");
    internal static GitHubFailure Upstream() => new(502, "upstream_error");
}
