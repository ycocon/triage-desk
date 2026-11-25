using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

internal sealed class VerificationInbox(UserManager<Account> users, IConfiguration config, IWebHostEnvironment env,
    IHttpClientFactory clients)
{
    internal async Task SendAsync(Account account, CancellationToken ct)
    {
        if (account.EmailConfirmed || account.Disabled) return;
        var origin = new Uri(config["Identity:WebOrigin"] ?? "https://localhost:3000");
        var token = await users.GenerateEmailConfirmationTokenAsync(account);
        var url = new Uri(origin, $"/verify-email#userId={account.Id}&token={Uri.EscapeDataString(token)}");
        var address = account.Email!;
        if (!env.IsDevelopment())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
            {
                Content = JsonContent.Create(new { from = config["Mail:Sender"], to = new[] { address },
                    subject = "Verify your Triage Desk email", text = "Confirm your email: " + url.AbsoluteUri })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Mail:ApiKey"]);
            using var client = clients.CreateClient("verification");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Verification email delivery failed.");
            return;
        }

        var inbox = config["Identity:InboxDirectory"]
            ?? throw new InvalidOperationException("Set Identity__InboxDirectory for local verification.");
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
