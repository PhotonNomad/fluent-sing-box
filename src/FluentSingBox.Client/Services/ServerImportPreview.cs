using System.Text.Json;
using FluentSingBox.Configuration.Profiles;
using FluentSingBox.Configuration.SingBox;

namespace FluentSingBox.Client.Services;

public sealed record ServerImportEntry(int Line, Node? Node, string? Error)
{
    public string Summary => Node is null ? Localization.Format("ServerImportPreview_Line01", Line, Error) : Localization.Format("ServerImportPreview_Line0123", Line, Node.Name, ProfileText.Protocol(Node), ProfileText.Endpoint(Node));
}

public static class ServerImportPreview
{
    public static IReadOnlyList<ServerImportEntry> Parse(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return [new(1, null, Localization.Get("ServerImportPreview_PasteShareLinksOrSingBoxJSON"))];
        if (text.StartsWith('{') || text.StartsWith('[') || !text.Contains("://", StringComparison.Ordinal))
            return ParseItem(text, 1);
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').SelectMany((line, index) =>
            string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')
                ? [] : ParseItem(line, index + 1)).ToArray();
    }

    private static IReadOnlyList<ServerImportEntry> ParseItem(string input, int line)
    {
        IReadOnlyList<ImportedOutbound> outbounds;
        try { outbounds = ServerLinkImporter.Parse(input); }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            // Never echo a share link or parser exception: either can contain credentials.
            return [new(line, null, Localization.Get("ServerImportPreview_InvalidOrUnsupportedFormatCheckThisEntry"))];
        }
        return outbounds.Select(item =>
        {
            try { return new ServerImportEntry(line, SingBoxOutboundReader.Read(item.Outbound, ProfileLibrary.NewId(), item.Name), null); }
            catch (NotSupportedException unsupported) { return new ServerImportEntry(line, null, Localization.Format("ServerImportPreview_Unsupported", unsupported.Message)); }
            catch (FormatException) { return new ServerImportEntry(line, null, Localization.Get("ServerImportPreview_InvalidOrUnsupportedFormatCheckThisEntry")); }
        }).ToArray();
    }
}
