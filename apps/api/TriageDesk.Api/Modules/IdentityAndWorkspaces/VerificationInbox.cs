using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

internal sealed class VerificationInbox(UserManager<Account> users, IConfiguration config, IWebHostEnvironment env)
{
    internal async Task SendAsync(Account account, CancellationToken ct)
    {
        if (account.EmailConfirmed || account.Disabled) return;
        if (!env.IsDevelopment())
            throw new InvalidOperationException("Configure the hosted verification email sender before deployment.");

        var inbox = config["Identity:InboxDirectory"]
            ?? throw new InvalidOperationException("Set Identity__InboxDirectory for local verification.");
        var origin = new Uri(config["Identity:WebOrigin"] ?? "https://localhost:3000");
        var token = await users.GenerateEmailConfirmationTokenAsync(account);
        var url = new Uri(origin, $"/verify-email#userId={account.Id}&token={Uri.EscapeDataString(token)}");
        var address = account.Email!;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.ToUpperInvariant())))
            .ToLowerInvariant();

        Directory.CreateDirectory(inbox);
        var file = Path.Combine(inbox, $"{key}.json");
        var temp = Path.Combine(inbox, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new { to = address, url }), ct);
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
