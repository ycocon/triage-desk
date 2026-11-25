using System.Net.Http.Headers;

namespace TriageDesk.Api.Modules.RepositoriesAndImport;

internal sealed class GitHubReadBudget(TimeProvider clock)
{
    private const int Limit = 60;
    private readonly object gate = new();
    private int remaining = Limit;
    private DateTimeOffset resetAt;
    private DateTimeOffset blockedUntil;

    internal DateTimeOffset Now => clock.GetUtcNow();

    internal void Reserve()
    {
        lock (gate)
        {
            var now = Now;
            if (now < blockedUntil) throw Limited(blockedUntil - now);
            if (resetAt <= now)
            {
                resetAt = now.AddHours(1);
                remaining = Limit;
            }
            if (remaining == 0) throw Limited(resetAt - now);
            remaining--;
        }
    }

    internal void Observe(HttpResponseHeaders headers)
    {
        if (!TryRemaining(headers, out var observed) || Reset(headers) is not { } reset || reset <= Now)
            return;
        lock (gate)
        {
            remaining = Math.Min(remaining, Math.Clamp(observed, 0, Limit));
            resetAt = reset;
        }
    }

    internal void Block(TimeSpan delay)
    {
        lock (gate)
        {
            var until = Now + delay;
            if (until > blockedUntil) blockedUntil = until;
        }
    }

    internal static bool TryRemaining(HttpResponseHeaders headers, out int remaining)
    {
        remaining = 0;
        return headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
            int.TryParse(values.FirstOrDefault(), out remaining);
    }

    internal static DateTimeOffset? Reset(HttpResponseHeaders headers) =>
        headers.TryGetValues("X-RateLimit-Reset", out var values) &&
        long.TryParse(values.FirstOrDefault(), out var time) && time > 0 && time <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(time) : null;

    internal static GitHubFailure Limited(TimeSpan delay) => new(429, "rate_limited", delay);
}
