using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Diagnostics;
using System.Text.Json.Serialization;
using TriageDesk.Api.Http;
using TriageDesk.Api.Modules.IdentityAndWorkspaces;
using TriageDesk.Api.Modules.RepositoriesAndImport;
using TriageDesk.Api.Modules.TriageAndActivity;
using TriageDesk.Api.Operations;
using TriageDesk.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("TriageDesk")
    ?? throw new InvalidOperationException("Set ConnectionStrings__TriageDesk.");
var database = new NpgsqlConnectionStringBuilder(connection);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(database.ConnectionString));
builder.Services.AddDbContext<TriageDeskDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<NpgsqlDataSource>(), pg => pg.SetPostgresVersion(18, 0)));
ReleaseOperations.Configure(builder, database);

builder.Services.AddIdentityCore<Account>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.User.AllowedUserNameCharacters = string.Empty;
    options.SignIn.RequireConfirmedEmail = true;
    options.Password.RequiredLength = 12;
    options.Password.RequiredUniqueChars = 1;
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
})
.AddEntityFrameworkStores<TriageDeskDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
    options.TokenLifespan = TimeSpan.FromHours(24));
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "__Host-td-session";
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context =>
            ApiProblem.Write(context.HttpContext, 401, "unauthenticated", "Sign in to continue.");
        options.Events.OnRedirectToAccessDenied = context =>
            ApiProblem.Write(context.HttpContext, 403, "forbidden", "You cannot perform this action.");
        options.Events.OnValidatePrincipal = async context =>
        {
            var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<Account>>();
            var account = await signIn.ValidateSecurityStampAsync(context.Principal);
            if (account is not { Disabled: false, EmailConfirmed: true })
                context.RejectPrincipal();
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "__Host-td-csrf";
    options.Cookie.Path = "/";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddSingleton<AccountThrottle>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<WorkspaceAccess>();
builder.Services.AddScoped<VerificationInbox>();
builder.Services.AddHttpClient("verification", settings => settings.Timeout = TimeSpan.FromSeconds(8))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<GitHubReadBudget>();
var github = builder.Services.AddHttpClient<GitHubIssuesClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(12);
    client.MaxResponseContentBufferSize = 12 * 1024 * 1024;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TriageDesk/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});
github.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
if (builder.Environment.IsDevelopment() && builder.Configuration["GitHub:FixtureOrigin"] is { } configuredFixture)
{
    var origin = new Uri(configuredFixture);
    if (!origin.IsLoopback || origin.Scheme != "http" || origin.UserInfo.Length > 0 ||
        origin.PathAndQuery != "/" || origin.Fragment.Length > 0)
        throw new InvalidOperationException("The GitHub fixture must be a local HTTP origin.");
    github.AddHttpMessageHandler(() => new LocalGitHubFixtureHandler(origin));
}

var app = builder.Build();
var release = app.Services.GetRequiredService<ReleaseOperations>();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.CacheControl = "private, no-store";
        return Task.CompletedTask;
    });
    var started = Stopwatch.GetTimestamp();
    await next();
    if (release.StructuredLogs && context.Request.Path.StartsWithSegments("/api/v1"))
        app.Logger.LogInformation("Request {Method} {Action} {Outcome} in {DurationMs} ms ({TraceId}, {Release})",
            context.Request.Method, (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier, release.Release);
});
app.UseExceptionHandler(error => error.Run(context =>
{
    var issue = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    return issue is BadHttpRequestException { StatusCode: 400 }
        ? ApiProblem.Write(context, 400, "validation_failed", "Check the request fields.")
        : ApiProblem.Write(context, 500, "internal_error", "The request could not be completed.");
}));
app.UseStatusCodePages(context => ApiProblem.WriteStatus(context.HttpContext));
app.UseAuthentication();
app.UseAuthorization();
ReleaseOperations.MapReleaseProbes(app);

var api = app.MapGroup("/api/v1");
api.AddEndpointFilter(async (invocation, next) =>
{
    var context = invocation.HttpContext;
    if (HttpMethods.IsGet(context.Request.Method))
        return await next(invocation);
    if (release.RecoveryMode)
        return ApiProblem.Result(context, 503, "recovery_mode", "The workspace is temporarily read-only.");

    var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
    if (!context.Request.Headers.ContainsKey("X-CSRF-Token") ||
        !await antiforgery.IsRequestValidAsync(context))
        return ApiProblem.Result(context, 403, "csrf_failed", "Refresh and try again.");

    return await next(invocation);
});
api.MapAccounts();
api.MapWorkspaces();
api.MapTeamMembers();
api.MapInvitations();
api.MapRepositories();
api.MapImports();
api.MapIssues();
api.MapIssueDecisions();
api.MapWorkspaceActivity();
api.MapIssueReports();
app.Run();

public partial class Program;
