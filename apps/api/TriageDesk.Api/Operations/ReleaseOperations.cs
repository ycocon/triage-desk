using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace TriageDesk.Api.Operations;

internal sealed record ReleaseOperations(string Release, bool RecoveryMode, bool StructuredLogs)
{
    internal const string MinimumMigration = "20261125155813_DurableProtectionKeys";

    internal static void Configure(WebApplicationBuilder builder, NpgsqlConnectionStringBuilder database)
    {
        var config = builder.Configuration;
        var production = builder.Environment.IsProduction();
        var release = config["Operations:Release"] ?? "local";
        if (release != "local" && (release.Length != 40 || !release.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("Operations__Release must be the 40-character Git commit.");
        var recovery = config.GetValue<bool>("Operations:RecoveryMode");
        var structured = production || config.GetValue<bool>("Operations:StructuredLogs");
        builder.Services.AddSingleton(new ReleaseOperations(release, recovery, structured));

        if (production)
        {
            if (release == "local" || database.SslMode != SslMode.VerifyFull || !database.Pooling || database.MaxPoolSize > 10)
                throw new InvalidOperationException("Production requires a release ID and a verified TLS database pool capped at 10.");
            if (!Uri.TryCreate(config["Identity:WebOrigin"], UriKind.Absolute, out var origin) ||
                origin.Scheme != Uri.UriSchemeHttps || origin.IsLoopback || origin.UserInfo.Length > 0 ||
                origin.PathAndQuery != "/" || origin.Fragment.Length > 0)
                throw new InvalidOperationException("Set Identity__WebOrigin to the hosted HTTPS origin.");
            if (!recovery && (string.IsNullOrWhiteSpace(config["Mail:ApiKey"]) || string.IsNullOrWhiteSpace(config["Mail:Sender"])))
                throw new InvalidOperationException("Configure the verified mail sender.");
        }
        if (structured)
        {
            builder.Logging.AddJsonConsole(options => { options.UseUtcTimestamp = true; options.TimestampFormat = "O"; });
            // Log route templates and operational metadata, without framework request URLs or exception payloads.
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
            builder.Logging.AddFilter("System.Net.Http", LogLevel.None);
            builder.Logging.AddFilter("Npgsql", LogLevel.None);
        }

        var protection = builder.Services.AddDataProtection().SetApplicationName("TriageDesk");
        if (config["Identity:KeyCertificate"] is { } encoded)
        {
            var certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(encoded),
                config["Identity:KeyCertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey) throw new InvalidOperationException("The key wrapping certificate needs its private key.");
            protection.ProtectKeysWithCertificate(certificate);
            if (config["Identity:PreviousKeyCertificate"] is { } previous)
            {
                var priorCertificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(previous),
                    config["Identity:PreviousKeyCertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
                protection.UnprotectKeysWithAnyCertificate(certificate, priorCertificate);
            }
            builder.Services.AddOptions<KeyManagementOptions>().Configure<NpgsqlDataSource>((options, source) =>
                options.XmlRepository = new ProtectionKeyStore(source));
        }
        else if (production)
            throw new InvalidOperationException("Configure the PostgreSQL key wrapping certificate.");
        else if (config["Identity:KeyDirectory"] is { } directory)
            protection.PersistKeysToFileSystem(Directory.CreateDirectory(directory));
    }

    internal static void MapReleaseProbes(WebApplication app)
    {
        app.MapGet("/health/live", (HttpContext context, ReleaseOperations release) => Probe(context, release, true));
        app.MapGet("/health/ready", async (HttpContext context, ReleaseOperations release,
            NpgsqlDataSource database, ILogger<ReleaseOperations> logger) =>
        {
            if (release.RecoveryMode) return Probe(context, release, false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await using var command = database.CreateCommand(
                    """
                    SELECT current_setting('server_version_num')::integer >= 180000 AND EXISTS (
                        SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = $1
                    )
                    """);
                command.Parameters.AddWithValue(MinimumMigration);
                return Probe(context, release, await command.ExecuteScalarAsync(limit.Token) is true);
            }
            catch (Exception error) when (error is NpgsqlException or TimeoutException or OperationCanceledException)
            {
                logger.LogWarning("Database readiness failed ({FailureType})", error.GetType().Name);
                return Probe(context, release, false);
            }
        });
    }

    private static IResult Probe(HttpContext context, ReleaseOperations release, bool ready)
    {
        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusPages) statusPages.Enabled = false;
        context.Response.Headers["X-TriageDesk-Release"] = release.Release;
        return Results.StatusCode(ready ? StatusCodes.Status204NoContent : StatusCodes.Status503ServiceUnavailable);
    }
}
