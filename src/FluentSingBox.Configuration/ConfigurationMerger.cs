using System.Text.Json.Nodes;

namespace FluentSingBox.Configuration;

// Three-way merge of a regenerated configuration into the user-editable source:
// apply the difference between the previous and next generated documents to the
// current document, preserving edits the user made by hand.
public static class ConfigurationMerger
{
    public static void ApplyChanges(JsonObject current, JsonObject previous, JsonObject next)
    {
        foreach (var key in previous.Select(pair => pair.Key).Union(next.Select(pair => pair.Key)).ToArray())
        {
            var hadPrevious = previous.TryGetPropertyValue(key, out var oldValue);
            var hasNext = next.TryGetPropertyValue(key, out var newValue);
            if (hadPrevious == hasNext && JsonNode.DeepEquals(oldValue, newValue)) continue;
            if (!hasNext) current.Remove(key);
            else if (oldValue is JsonObject oldObject && newValue is JsonObject newObject && current[key] is JsonObject target)
                ApplyChanges(target, oldObject, newObject);
            else if (oldValue is JsonArray oldArray && newValue is JsonArray newArray && current[key] is JsonArray targetArray
                && oldArray.Concat(newArray).All(item => item is JsonObject obj && obj["tag"] is JsonValue))
                ApplyTaggedChanges(targetArray, oldArray, newArray);
            else if (key == "rules" && oldValue is JsonArray oldRules && newValue is JsonArray newRules && current[key] is JsonArray targetRules)
                ApplyRuleChanges(targetRules, oldRules, newRules);
            else current[key] = newValue?.DeepClone();
        }
    }

    // Form-owned rules may change while a source editor adds its own rules.
    // Only replace rules still identical to the previous generated version.
    private static void ApplyRuleChanges(JsonArray current, JsonArray previous, JsonArray next)
    {
        foreach (var old in previous.Where(item => !next.Any(candidate => JsonNode.DeepEquals(item, candidate))))
        {
            var index = current.ToList().FindIndex(item => JsonNode.DeepEquals(item, old));
            if (index >= 0) current.RemoveAt(index);
        }
        for (var position = 0; position < next.Count; position++)
        {
            var added = next[position];
            if (previous.Any(item => JsonNode.DeepEquals(item, added)) || current.Any(item => JsonNode.DeepEquals(item, added))) continue;
            var insertion = -1;
            for (var before = position - 1; before >= 0 && insertion < 0; before--)
            {
                var index = current.ToList().FindIndex(item => JsonNode.DeepEquals(item, next[before]));
                if (index >= 0) insertion = index + 1;
            }
            // No predecessor survived a manual edit: anchor before a successor.
            for (var after = position + 1; after < next.Count && insertion < 0; after++)
                insertion = current.ToList().FindIndex(item => JsonNode.DeepEquals(item, next[after]));
            current.Insert(insertion < 0 ? 0 : insertion, added?.DeepClone());
        }
    }

    private static void ApplyTaggedChanges(JsonArray current, JsonArray previous, JsonArray next)
    {
        foreach (var old in previous.OfType<JsonObject>())
        {
            var tag = old["tag"]!.ToString();
            var replacement = next.OfType<JsonObject>().FirstOrDefault(item => item["tag"]?.ToString() == tag);
            var target = current.OfType<JsonObject>().FirstOrDefault(item => item["tag"]?.ToString() == tag);
            if (replacement is null) { if (target is not null) current.Remove(target); }
            else if (target is not null) ApplyChanges(target, old, replacement);
            else if (!JsonNode.DeepEquals(old, replacement)) current.Add(replacement.DeepClone());
        }
        foreach (var added in next.OfType<JsonObject>().Where(item => !previous.OfType<JsonObject>().Any(old => old["tag"]?.ToString() == item["tag"]?.ToString())))
        {
            var existing = current.OfType<JsonObject>().FirstOrDefault(item => item["tag"]?.ToString() == added["tag"]?.ToString());
            if (existing is not null) current.Remove(existing);
            current.Add(added.DeepClone());
        }
    }
}
