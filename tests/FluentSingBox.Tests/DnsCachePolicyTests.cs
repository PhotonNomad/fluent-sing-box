using FluentSingBox.Client.Services;

namespace FluentSingBox.Tests;

public class DnsCachePolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fluent-dns-test-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(_directory, "cache.db");
    private string Pending => Path.Combine(_directory, "dns-cache-reset.pending");

    public DnsCachePolicyTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Cache, "fixture");
    }

    [Fact]
    public void RunningCoreKeepsDatabaseAndQueuesClearUntilNextStart()
    {
        Assert.False(DnsCachePolicy.RequestClear(_directory, coreRunning: true));
        Assert.Equal("fixture", File.ReadAllText(Cache));
        Assert.True(File.Exists(Pending));

        DnsCachePolicy.ClearBeforeStart(_directory);

        Assert.False(File.Exists(Cache));
        Assert.False(File.Exists(Pending));
    }

    [Fact]
    public void StoppedCoreClearsImmediately()
    {
        Assert.True(DnsCachePolicy.RequestClear(_directory, coreRunning: false));
        Assert.False(File.Exists(Cache));
        Assert.False(File.Exists(Pending));
    }

    [Fact]
    public void LockedDatabaseKeepsPendingRequestAndReportsFailure()
    {
        using (var held = new FileStream(Cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => DnsCachePolicy.RequestClear(_directory, coreRunning: false));
            Assert.True(File.Exists(Pending));
        }

        DnsCachePolicy.ClearBeforeStart(_directory);
        Assert.False(File.Exists(Cache));
        Assert.False(File.Exists(Pending));
    }

    [Fact]
    public void StartupWithoutPendingClearPreservesCache()
    {
        DnsCachePolicy.ClearBeforeStart(_directory);
        Assert.Equal("fixture", File.ReadAllText(Cache));
    }

    [Theory]
    [InlineData("domain_resolver", "other-direct-dns")]
    [InlineData("server", "changed.example")]
    public void NodeResolverAndProxyEndpointChangesInvalidatePersistentAnswers(string key, string value)
    {
        var config = System.Text.Json.Nodes.JsonNode.Parse("""
            {"dns":{"servers":[]},"outbounds":[{"type":"shadowsocks","tag":"proxy","server":"edge.example","domain_resolver":"system"}],"route":{"final":"proxy"}}
            """)!;
        var original = DnsCachePolicy.IdentityHash(config.ToJsonString());
        config["outbounds"]![0]![key] = value;

        Assert.NotEqual(original, DnsCachePolicy.IdentityHash(config.ToJsonString()));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
