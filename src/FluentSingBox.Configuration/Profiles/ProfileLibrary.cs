using System.Text;

namespace FluentSingBox.Configuration.Profiles;

public sealed record ProfileEntry(string Id, string Name, string Path, string? Error);

// Stores each profile as profiles/<id>.json. Writes are atomic and keep one backup; a
// deleted profile is moved aside rather than erased.
public sealed class ProfileLibrary(string directory)
{
    public string Directory { get; } = directory;

    public string PathFor(string id) => System.IO.Path.Combine(Directory, id + ".json");

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public IReadOnlyList<ProfileEntry> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var entries = new List<ProfileEntry>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var id = System.IO.Path.GetFileNameWithoutExtension(path);
            try { entries.Add(new(id, Load(id).Name, path, null)); }
            catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            { entries.Add(new(id, id, path, error.Message)); }
        }
        return entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }

    // The file name is the identity; an id inside the file that disagrees is corrected on load.
    public Profile Load(string id)
    {
        var profile = ProfileJson.Deserialize(File.ReadAllText(PathFor(id)));
        if (profile.FormatVersion != Profile.CurrentFormatVersion) throw new InvalidDataException($"formatVersion {profile.FormatVersion}");
        profile.Id = id;
        return profile;
    }

    // Returns the exact bytes written, so a file watcher can recognise its own write.
    public byte[] Save(Profile profile)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(profile.Id);
        var bytes = Encoding.UTF8.GetBytes(ProfileJson.Serialize(profile));
        var temporary = path + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".backup", ignoreMetadataErrors: true);
        else File.Move(temporary, path);
        return bytes;
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return;
        var trash = System.IO.Path.Combine(Directory, "deleted");
        System.IO.Directory.CreateDirectory(trash);
        File.Move(path, System.IO.Path.Combine(trash, $"{id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"));
        if (File.Exists(path + ".backup")) File.Delete(path + ".backup");
    }
}
