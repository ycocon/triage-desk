using System.Diagnostics;

namespace TriageDesk.Api.Http;

internal static class ApiProblem
{
    internal static IResult Result(HttpContext context, int status, string code, string title,
        Dictionary<string, object?>? extensions = null)
    {
        extensions ??= new();
        extensions["code"] = code;
        extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        return Results.Problem(statusCode: status, type: $"urn:triagedesk:problem:{code}", title: title,
            extensions: extensions);
    }

    internal static Task Write(HttpContext context, int status, string code, string title) =>
        Result(context, status, code, title).ExecuteAsync(context);

    internal static IResult Invalid(HttpContext context) =>
        Result(context, 400, "validation_failed", "Check the request fields.");

    internal static Task WriteStatus(HttpContext context) => context.Response.StatusCode switch
    {
        400 => Write(context, 400, "validation_failed", "Check the request fields."),
        404 => Write(context, 404, "not_found", "This resource could not be found."),
        405 => Write(context, 405, "method_not_allowed", "This method is unavailable."),
        415 => Write(context, 415, "unsupported_media_type", "Use application/json."),
        _ => Task.CompletedTask
    };
}
