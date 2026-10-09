using FluentSingBox.Client.Models;
using System.Text.Json;

namespace FluentSingBox.Client.Services;

public sealed record ServerImportEntry(int Line, ServerProfile? Server, string? Error)
{
    public string Summary => Server is null ? Localization.Format("ServerImportPreview_Line01", Line, Error) : Localization.Format("ServerImportPreview_Line0123", Line, Server.Name, Server.ProtocolSummary, Server.Endpoint);
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
        try { return ServerLinkImporter.Parse(input).Select(server => new ServerImportEntry(line, server, null)).ToArray(); }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            // Never echo a share link or parser exception: either can contain credentials.
            return [new(line, null, Localization.Get("ServerImportPreview_InvalidOrUnsupportedFormatCheckThisEntry"))];
        }
    }
}
