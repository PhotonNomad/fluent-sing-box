using System.Collections;
using System.Reflection;

namespace FluentSingBox.Configuration.Profiles;

public static class ProfileSecrets
{
    // Every non-empty [Secret] value in the profile, for redacting logs and error messages.
    public static IReadOnlySet<string> Collect(Profile? profile)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        if (profile is not null) Visit(profile);
        return secrets;

        void Visit(object value)
        {
            // Scalar structs such as DateTimeOffset lead back to themselves through DateTime.Date.
            if (value is string || value.GetType().IsValueType) return;
            if (value is IEnumerable items)
            {
                foreach (var item in items) if (item is not null) Visit(item);
                return;
            }
            foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetValue(value) is not { } child) continue;
                if (child is string text)
                {
                    if (text.Length > 0 && property.IsDefined(typeof(SecretAttribute), inherit: true)) secrets.Add(text);
                }
                else Visit(child);
            }
        }
    }
}
