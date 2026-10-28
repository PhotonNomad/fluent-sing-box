using FluentSingBox.Client.Services;
using FluentSingBox.Configuration.Profiles;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Tests;

public class ShareLinkImportTests
{
    private static Node Import(string link)
    {
        Localization.Configure("en-US");
        var imported = Assert.Single(ServerLinkImporter.Parse(link));
        return SingBoxOutboundReader.Read(imported.Outbound, "id", imported.Name);
    }

    [Fact]
    public void Hysteria2PortHoppingIsKept()
    {
        var node = Assert.IsType<Hysteria2Node>(Import("hy2://secret@hy.example:443?sni=hy.example&mport=20000-30000,443#HY"));
        Assert.Equal(["20000-30000", "443"], node.ServerPorts);
    }

    [Fact]
    public void ShadowsocksPluginLinksImport()
    {
        var node = Assert.IsType<ShadowsocksNode>(Import("ss://YWVzLTEyOC1nY206cGFzcw@ss.example:8388?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dexample.com#SS"));
        Assert.Equal("obfs-local", node.Plugin);
        Assert.Equal("obfs=http;obfs-host=example.com", node.PluginOptions);
    }

    [Fact]
    public void VlessPacketEncodingIsKept()
    {
        var node = Assert.IsType<VlessNode>(Import("vless://11111111-1111-1111-1111-111111111111@v.example:443?security=tls&packetEncoding=xudp#V"));
        Assert.Equal("xudp", node.PacketEncoding);
    }

    [Fact]
    public void Http2TransportLinksImport()
    {
        var node = Assert.IsType<VlessNode>(Import("vless://11111111-1111-1111-1111-111111111111@v.example:443?security=tls&type=h2&host=a.example&path=/h2#V"));
        var transport = Assert.IsType<Http2Transport>(node.Transport);
        Assert.Equal(["a.example"], transport.Hosts);
        Assert.Equal("/h2", transport.Path);
    }
}
