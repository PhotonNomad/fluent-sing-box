using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using FluentSingBox.Client.Models;

namespace FluentSingBox.Client.Services;

// Restore existing model instances so bindings do not keep an invalid edited object.
internal static class SettingsCheckpoint
{
    public static void Restore(object target, object saved)
    {
        foreach (var property in saved.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null
                || property.GetCustomAttribute<KeepOnRollbackAttribute>() is not null) continue;
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
                destination.Clear();
                foreach (var entry in restored) destination.Add(entry);
            }
            else if (property.CanWrite) property.SetValue(target, value);
        }
    }
}
