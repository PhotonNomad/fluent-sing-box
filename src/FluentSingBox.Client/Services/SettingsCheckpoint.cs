using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// Restore existing model instances so bindings do not keep an invalid edited object.
internal static class SettingsCheckpoint
{
    public static AppSettingsData Snapshot(AppSettingsData settings)
    {
        var copy = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
        for (var index = 0; index < settings.Servers.Count; index++)
            copy.Servers[index].FromUserConfig = settings.Servers[index].FromUserConfig;
        return copy;
    }

    public static void ApplyReload(AppSettingsData target, AppSettingsData incoming)
    {
        // Disk preferences do not own an active Windows proxy session or its recovery snapshot.
        incoming.SystemProxyEnabled = target.SystemProxyEnabled;
        incoming.HasSystemProxySnapshot = target.HasSystemProxySnapshot;
        incoming.OwnedProxyServer = target.OwnedProxyServer;
        incoming.PreviousProxyEnable = target.PreviousProxyEnable;
        incoming.PreviousConnectionFlags = target.PreviousConnectionFlags;
        incoming.PreviousProxyServer = target.PreviousProxyServer;
        incoming.PreviousProxyOverride = target.PreviousProxyOverride;
        incoming.PreviousAutoConfigUrl = target.PreviousAutoConfigUrl;
        incoming.PreviousProxyEnablePresent = target.PreviousProxyEnablePresent;
        incoming.ResumeCoreOnLaunch = target.ResumeCoreOnLaunch;
        incoming.ResumeSystemProxyOnLaunch = target.ResumeSystemProxyOnLaunch;
        Restore(target, incoming);
    }
    public static bool SameContent(AppSettingsData a, AppSettingsData b)
        => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    public static void Restore(object target, object saved)
    {
        if (target is ServerProfile server && saved is ServerProfile sourceServer)
            server.FromUserConfig = sourceServer.FromUserConfig;
        foreach (var property in saved.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
            var value = property.GetValue(saved);
            var current = property.GetValue(target);
            if (value is IList source && current is IList destination && !destination.IsReadOnly && !destination.IsFixedSize)
            {
                var items = destination.Cast<object>().ToArray();
                var restored = new List<object>();
                foreach (var entry in source)
                {
                    var id = entry?.GetType().GetProperty("Id");
                    var existing = id is null ? null : items.FirstOrDefault(item => Equals(id.GetValue(item), id.GetValue(entry)));
                    if (existing is not null) { Restore(existing, entry!); restored.Add(existing); }
                    else restored.Add(entry!);
                }
                if (!items.SequenceEqual(restored))
                {
                    destination.Clear();
                    foreach (var entry in restored) destination.Add(entry);
                }
            }
            else if (property.CanWrite) property.SetValue(target, value);
        }
    }
}
