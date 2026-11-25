using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using Xunit;

namespace TriageDesk.Api.IntegrationTests;

public sealed class AccountSessionTests
{
    [Fact]
    public async Task SessionExpiresEightHoursAfterLoginEvenWhenUsed()
    {
        var clock = new SessionClock();
        using var host = CreateHost(clock);
        var account = await SeedAsync(host);
        using var browser = CreateClient(host);
        await SignInAsync(browser, account.Email!);

        clock.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(59));
        using var valid = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.False(valid.Headers.Contains("Set-Cookie"));

        clock.Advance(TimeSpan.FromMinutes(2));
        using var expired = await browser.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task RevokedSecurityStampIsRejectedOnTheNextRequest()
    {
        using var host = CreateHost(new SessionClock());
        var account = await SeedAsync(host);
        using var browser = CreateClient(host);
        await SignInAsync(browser, account.Email!);

        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var current = await users.FindByIdAsync(account.Id.ToString());
            Assert.True((await users.UpdateSecurityStampAsync(current!)).Succeeded);
        }

        using var denied = await browser.GetAsync("/api/v1/workspaces");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    private const string Password = "local test password 2026";
    private static HttpClient CreateClient(WebApplicationFactory<Program> host) => host.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private static WebApplicationFactory<Program> CreateHost(TimeProvider clock) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme,
                options => options.TimeProvider = clock);
        }));

    private static async Task<Account> SeedAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var email = $"{Guid.NewGuid():N}@example.test";
        var account = new Account { Id = Guid.CreateVersion7(), Email = email, UserName = email,
            DisplayName = "Test owner", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(account, Password)).Succeeded);
        return account;
    }

    private static async Task SignInAsync(HttpClient client, string email)
    {
        var token = await client.GetFromJsonAsync<Csrf>("/api/v1/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login");
        request.Headers.Add("X-CSRF-Token", token!.RequestToken);
        request.Content = JsonContent.Create(new { email, password = Password });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed record Csrf(string RequestToken);
    private sealed class SessionClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan period) => now += period;
    }
}
