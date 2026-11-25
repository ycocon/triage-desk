namespace TriageDesk.Api.Modules.RepositoriesAndImport;

// This handler can be enabled only by the local Development host.
internal sealed class LocalGitHubFixtureHandler(Uri fixtureOrigin) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = new Uri(fixtureOrigin, request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, cancellationToken);
    }
}
