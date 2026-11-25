using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TriageDesk.Api.Http;

namespace TriageDesk.Api.Modules.IdentityAndWorkspaces;

internal sealed record RegisterAccount(string? Email, string? Password, string? DisplayName);
internal sealed record SignInAccount(string? Email, string? Password);
internal sealed record EmailRequest(string? Email);
internal sealed record ConfirmEmail(Guid UserId, string? Token);
internal sealed record Preferences(string? Theme);
internal sealed record AccountView(Guid UserId, string DisplayName, string Email, bool EmailVerified, string Theme);

internal static class Accounts
{
    internal static void MapAccounts(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(context).RequestToken }));
        auth.MapPost("/register", Register);
        auth.MapPost("/email-verification/requests", RequestVerification);
        auth.MapPost("/email-verification", Confirm);
        auth.MapPost("/login", Login);
        auth.MapPost("/logout", async (SignInManager<Account> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        api.MapGet("/me", async (HttpContext context, UserManager<Account> users) =>
            Results.Ok(ToView((await users.GetUserAsync(context.User))!))).RequireAuthorization();
        api.MapPatch("/me/preferences", SavePreferences).RequireAuthorization();
    }

    internal static bool ValidName(string? value) =>
        value is { Length: >= 1 and <= 80 } && !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsControl);

    internal static string? ValidEmail(string? value) =>
        value is { Length: >= 1 and <= 254 } && new EmailAddressAttribute().IsValid(value.Trim()) &&
        !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl) ? value : null;

    private static AccountView ToView(Account user) =>
        new(user.Id, user.DisplayName, user.Email!, user.EmailConfirmed, user.Theme);

    private static async Task<IResult> Register(RegisterAccount request, HttpContext context,
        UserManager<Account> users, AccountThrottle limits, VerificationInbox inbox)
    {
        var email = ValidEmail(request.Email);
        if (email is null || request.Password is not { Length: >= 12 and <= 128 } ||
            (request.DisplayName is not null && !ValidName(request.DisplayName)))
            return ApiProblem.Invalid(context);
        if (!limits.AllowClient(context, "email")) return Limited(context, 900);

        var account = await users.FindByEmailAsync(email);
        if (account is null)
        {
            var candidate = new Account
            {
                Id = Guid.CreateVersion7(), Email = email, UserName = email,
                DisplayName = request.DisplayName?.Trim() ?? "New member"
            };

            try
            {
                var result = await users.CreateAsync(candidate, request.Password);
                if (result.Succeeded) account = candidate;
                else if (result.Errors.Any(e => e.Code is not ("DuplicateEmail" or "DuplicateUserName")))
                    return ApiProblem.Invalid(context);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UserNameIndex" })
            {
                // A simultaneous registration already created the account.
            }
        }
        // Resending is a separate, generic operation. Re-registering never changes an account.
        if (account is not null && limits.AllowMail(users.NormalizeEmail(email)!))
            await inbox.SendAsync(account, context.RequestAborted);
        return Results.Accepted();
    }

    private static async Task<IResult> RequestVerification(EmailRequest request, HttpContext context,
        UserManager<Account> users, AccountThrottle limits, VerificationInbox inbox)
    {
        var email = ValidEmail(request.Email);
        if (email is null) return ApiProblem.Invalid(context);
        if (!limits.AllowClient(context, "email")) return Limited(context, 900);

        if (limits.AllowMail(users.NormalizeEmail(email)!) && await users.FindByEmailAsync(email) is { } account)
            await inbox.SendAsync(account, context.RequestAborted);
        return Results.Accepted();
    }

    private static async Task<IResult> Confirm(ConfirmEmail request, HttpContext context,
        UserManager<Account> users, AccountThrottle limits)
    {
        if (request.UserId == Guid.Empty || request.Token is not { Length: >= 1 and <= 4096 })
            return ApiProblem.Invalid(context);
        if (!limits.AllowClient(context, "confirm")) return Limited(context, 900);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var account = await users.FindByIdAsync(request.UserId.ToString());
            if (account is not { Disabled: false }) break;
            // Identity confirmation validates the token even if the account is already confirmed.
            var result = await users.ConfirmEmailAsync(account, request.Token);
            if (result.Succeeded) return Results.NoContent();
            if (!result.Errors.Any(e => e.Code == "ConcurrencyFailure")) break;
            var db = context.RequestServices.GetRequiredService<Persistence.TriageDeskDbContext>();
            db.Entry(account).State = EntityState.Detached;
        }
        return ApiProblem.Result(context, 410, "verification_expired", "Request a new verification email.");
    }

    private static async Task<IResult> Login(SignInAccount request, HttpContext context,
        UserManager<Account> users, SignInManager<Account> signIn, AccountThrottle limits)
    {
        var email = ValidEmail(request.Email);
        if (email is null || request.Password is not { Length: >= 1 and <= 128 })
            return ApiProblem.Invalid(context);
        if (!limits.AllowClient(context, "login")) return Limited(context, 900);

        var normalized = users.NormalizeEmail(email)!;
        if (limits.AccountBlocked(normalized)) return InvalidCredentials(context);

        var account = await users.FindByEmailAsync(email);
        if (account is { Disabled: false, EmailConfirmed: true })
        {
            if (await users.CheckPasswordAsync(account, request.Password))
            {
                await signIn.SignInAsync(account, isPersistent: false);
                return Results.NoContent();
            }
        }
        else
        {
            limits.CheckDummyPassword(request.Password);
        }

        limits.RecordFailure(normalized);
        return InvalidCredentials(context);
    }

    private static IResult InvalidCredentials(HttpContext context) =>
        ApiProblem.Result(context, 401, "unauthenticated",
            "Sign-in failed. Check your details and verify your email.");

    private static IResult Limited(HttpContext context, int seconds)
    {
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ApiProblem.Result(context, 429, "rate_limited", "Too many requests. Try again later.");
    }

    private static async Task<IResult> SavePreferences(Preferences request, HttpContext context,
        UserManager<Account> users)
    {
        if (request.Theme is not ("system" or "light" or "dark")) return ApiProblem.Invalid(context);
        var account = (await users.GetUserAsync(context.User))!;
        account.Theme = request.Theme;
        var result = await users.UpdateAsync(account);
        return result.Succeeded
            ? Results.Ok(ToView(account))
            : ApiProblem.Result(context, 409, "preferences_changed", "Reload and try again.");
    }
}
