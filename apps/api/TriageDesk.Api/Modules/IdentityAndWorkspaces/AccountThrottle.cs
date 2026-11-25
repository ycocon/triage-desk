using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

internal sealed class AccountThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string> clients = Create(30, TimeSpan.FromMinutes(15));
    private readonly PartitionedRateLimiter<string> failures = Create(5, TimeSpan.FromMinutes(15));
    private readonly PartitionedRateLimiter<string> mail = Create(3, TimeSpan.FromHours(1));
    private readonly PartitionedRateLimiter<string> invitations = Create(20, TimeSpan.FromMinutes(15));
    private readonly Account dummy = new() { DisplayName = "Unknown account" };
    private readonly PasswordHasher<Account> hasher = new();
    private readonly string dummyHash;

    public AccountThrottle() =>
        dummyHash = hasher.HashPassword(dummy, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    internal bool AllowClient(HttpContext context, string action) =>
        Acquire(clients, $"{action}:{context.Connection.RemoteIpAddress}");
    internal bool AllowMail(string email) => Acquire(mail, email);
    internal bool AllowInvitation(Guid caller, string action) => Acquire(invitations, $"{action}:{caller}");
    internal bool AccountBlocked(string email) => failures.GetStatistics(email)?.CurrentAvailablePermits == 0;
    internal void RecordFailure(string email) => Acquire(failures, email);
    internal void CheckDummyPassword(string password) => hasher.VerifyHashedPassword(dummy, dummyHash, password);

    private static bool Acquire(PartitionedRateLimiter<string> limiter, string key)
    {
        using var lease = limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    private static PartitionedRateLimiter<string> Create(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits, Window = window, QueueLimit = 0
            }));

    public void Dispose()
    {
        clients.Dispose();
        failures.Dispose();
        mail.Dispose();
        invitations.Dispose();
    }
}
