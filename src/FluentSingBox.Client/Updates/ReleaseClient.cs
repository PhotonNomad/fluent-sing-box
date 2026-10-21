using System.Net;
using System.Net.Http;
using FluentSingBox.Updates;

namespace FluentSingBox.Client.Services;

// GitHub Releases access. Every request is built and validated by UpdateProtocol, which
// also decides whether the private-repository token may be attached.
internal sealed class ReleaseClient
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(15) };

    public void SetUserAgent(string version) => _client.DefaultRequestHeaders.UserAgent.ParseAdd($"FluentSingBox/{version}");

    // Returns (false, null) when the repository has no published release.
    public async Task<(bool HasRelease, UpdateRelease? Release)> GetLatestAsync(string repository, string currentVersion, string scope, string? token, CancellationToken cancellation)
    {
        using var request = UpdateProtocol.CreateApiRequest(repository, $"https://api.github.com/repos/{repository}/releases/latest", token);
        using var response = await _client.SendAsync(request, cancellation);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidDataException(Localization.Get("Updates_AccessDenied"));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // GitHub also returns 404 for private repositories to unauthenticated clients.
            using var repositoryRequest = UpdateProtocol.CreateApiRequest(repository, $"https://api.github.com/repos/{repository}", token);
            using var repositoryResponse = await _client.SendAsync(repositoryRequest, cancellation);
            if (repositoryResponse.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidDataException(Localization.Get("Updates_RepositoryUnavailable"));
            repositoryResponse.EnsureSuccessStatusCode();
            return (false, null);
        }
        response.EnsureSuccessStatusCode();
        return (true, UpdateProtocol.SelectRelease(await response.Content.ReadAsStringAsync(cancellation), currentVersion, scope, repository));
    }

    public Task DownloadAsync(UpdateRelease release, string repository, string destination, IProgress<double> progress, CancellationToken cancellation, string? token) =>
        UpdateProtocol.DownloadAsync(_client, release, repository, destination, progress, cancellation, token);
}
