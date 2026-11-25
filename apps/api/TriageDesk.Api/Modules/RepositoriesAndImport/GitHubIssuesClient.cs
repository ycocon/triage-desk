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
        if (Delay is { } value)
            context.Response.Headers.RetryAfter = Math.Ceiling(value.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var title = Status switch
        {
            404 => "This public repository could not be found.",
            429 => "GitHub is limiting requests. Try again later.",
            _ => "GitHub did not return a usable response. Try again."
        };
        return ApiProblem.Result(context, Status, Code, title);
    }
}

internal sealed partial class GitHubIssuesClient(HttpClient http)
{
    private const int PageSize = 30;
    private static readonly Uri Origin = new("https://api.github.com");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();
    [GeneratedRegex("<([^>]+)>[ ]*;[ ]*rel=\"next\"", RegexOptions.CultureInvariant)]
    private static partial Regex NextLink();

    internal static bool ValidName(string? slug) =>
        slug is { Length: >= 3 and <= 140 } && RepositoryName().IsMatch(slug)
        && slug[(slug.IndexOf('/') + 1)..] is not ("." or "..")
        && !slug.Any(char.IsWhiteSpace);

    internal async Task<GitHubRepository> GetRepository(string slug, CancellationToken ct)
    {
        using var response = await Get(new Uri(Origin, "/repos/" + slug), metadata: true, ct);
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

    internal async Task<GitHubPage> GetPage(GitHubRepository repository, int page, CancellationToken ct)
    {
        var path = $"/repos/{repository.FullName}/issues?state=open&sort=created&direction=asc&per_page={PageSize}&page={page}";
        using var response = await Get(new Uri(Origin, path), metadata: false, ct);
        using var json = await Parse(response, ct);
        try
        {
            var root = json.RootElement;
            var issues = new List<GitHubIssue>();
            if (root.GetArrayLength() > PageSize) throw Upstream();
            foreach (var issue in root.EnumerateArray())
            {
                // GitHub returns pull requests from this endpoint as issue-shaped records.
                if (issue.TryGetProperty("pull_request", out _)) continue;
                var id = issue.GetProperty("id").GetInt64();
                var number = issue.GetProperty("number").GetInt32();
                var title = issue.GetProperty("title").GetString();
                var state = issue.GetProperty("state").GetString();
                if (id <= 0 || number <= 0 || title is null || state is not ("open" or "closed")) throw Upstream();
                if (state == "closed") continue;
                var labels = issue.GetProperty("labels").EnumerateArray()
                    .Select(label => label.GetProperty("name").GetString() ?? throw Upstream()).ToArray();
                issues.Add(new GitHubIssue(id, number, title, issue.GetProperty("body").GetString(),
                    state, labels, issue.GetProperty("created_at").GetDateTimeOffset().ToUniversalTime(),
                    issue.GetProperty("updated_at").GetDateTimeOffset().ToUniversalTime()));
            }
            return new GitHubPage(issues, root.GetArrayLength(), ParseNext(response, repository, page));
        }
        catch (Exception ex) when (InvalidShape(ex)) { throw Upstream(); }
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

    private async Task<HttpResponseMessage> Get(Uri uri, bool metadata, CancellationToken ct)
    {
        try
        {
            for (var redirects = 0; redirects <= 2; redirects++)
            {
                var response = await http.GetAsync(uri, ct);
                if (response.IsSuccessStatusCode) return response;
                using (response)
                {
                    if (metadata && (int)response.StatusCode is 301 or 302 or 307 or 308 &&
                        response.Headers.Location is { } location)
                    {
                        var destination = new Uri(uri, location);
                        if (!Trusted(destination) || destination.Query.Length > 0 || !MetadataPath(destination.AbsolutePath))
                            throw Upstream();
                        uri = destination;
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                        (response.StatusCode == HttpStatusCode.Forbidden &&
                            (response.Headers.RetryAfter is not null ||
                             response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))))
                    {
                        var delay = response.Headers.RetryAfter?.Delta;
                        if (delay is null && response.Headers.RetryAfter?.Date is { } retryOn)
                            delay = retryOn - DateTimeOffset.UtcNow;
                        if (delay is null && response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
                            long.TryParse(values.FirstOrDefault(), out var seconds) && seconds > 0 && seconds <= 253402300799)
                            delay = DateTimeOffset.FromUnixTimeSeconds(seconds) - DateTimeOffset.UtcNow;
                        throw new GitHubFailure(429, "rate_limited",
                            TimeSpan.FromSeconds(Math.Clamp(delay?.TotalSeconds ?? 60, 1, 86400)));
                    }
                    if (metadata && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) throw Missing();
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
