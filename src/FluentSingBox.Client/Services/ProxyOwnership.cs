namespace FluentSingBox.Client.Services;

public static class ProxyOwnership
{
    public static bool Matches(int enabled, string? server, string? bypass, string? pac, string expectedServer) =>
        enabled == 1 && string.Equals(server, expectedServer, StringComparison.OrdinalIgnoreCase)
        && string.Equals(bypass, "localhost;127.*;[::1]", StringComparison.Ordinal)
        && string.IsNullOrEmpty(pac);
}
