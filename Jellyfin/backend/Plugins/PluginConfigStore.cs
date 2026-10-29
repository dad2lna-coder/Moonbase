using System.Text.Json;
using System.Text.Json.Nodes;
using Moonfin.Server.Services;

namespace Moonfin.Server.Plugins;

/// <summary>
/// Admin config for one plugin. Secret values are stored on disk and never copied into a response.
/// </summary>
public static class PluginConfigStore
{
    public const string RedactedMarker = "********";

    public static bool IsSecretName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var compact = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("password", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("token", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("apikey", StringComparison.OrdinalIgnoreCase);
    }

    public static string ToClientJson(string? storedJson)
    {
        if (string.IsNullOrWhiteSpace(storedJson) || JsonNode.Parse(storedJson) is not JsonObject root)
        {
            return "{}";
        }

        var secretValues = new List<string>();
        CollectSecretValues(root, secretValues);

        var view = new JsonObject();
        foreach (var property in root)
        {
            if (string.Equals(property.Key, "secrets", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            view[property.Key] = RedactNode(property.Value, insideProxyRule: false);
        }

        if (root["secrets"] is JsonObject secrets)
        {
            var names = new JsonArray();
            foreach (var property in secrets)
            {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                {
                    names.Add(property.Key);
                }
            }

            view["secretNames"] = names;
        }
        else
        {
            view["secretNames"] = new JsonArray();
        }

        var json = view.ToJsonString();
        foreach (var secret in secretValues.Where(value => value.Length >= 8).Distinct(StringComparer.Ordinal))
        {
            json = json.Replace(secret, RedactedMarker, StringComparison.Ordinal);
        }

        return json;
    }

    /// <summary>
    /// Merges an admin update onto the stored document. A missing, null, or redacted secret keeps the stored value.
    /// An empty string clears it. Other fields replace.
    /// </summary>
    public static string Merge(string? storedJson, string incomingJson)
    {
        var stored = string.IsNullOrWhiteSpace(storedJson)
            ? new JsonObject()
            : JsonNode.Parse(storedJson) as JsonObject ?? new JsonObject();
        if (JsonNode.Parse(incomingJson) is not JsonObject incoming)
        {
            throw new InvalidDataException("Config must be a JSON object.");
        }

        var storedSecrets = stored["secrets"] as JsonObject ?? new JsonObject();
        var mergedSecrets = new JsonObject();
        foreach (var property in storedSecrets)
        {
            mergedSecrets[property.Key] = property.Value?.DeepClone();
        }

        if (incoming["secrets"] is JsonObject incomingSecrets)
        {
            foreach (var property in incomingSecrets)
            {
                if (property.Value is null)
                {
                    continue;
                }

                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (string.IsNullOrEmpty(text) )
                    {
                        mergedSecrets.Remove(property.Key);
                        continue;
                    }

                    if (text == RedactedMarker || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    mergedSecrets[property.Key] = text;
                    continue;
                }

                // Booleans and numbers are not secret values. Leave the stored secret alone.
            }
        }

        var merged = new JsonObject();
        foreach (var property in incoming)
        {
            if (string.Equals(property.Key, "secrets", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Key, "secretNames", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            merged[property.Key] = property.Value?.DeepClone();
        }

        merged["secrets"] = mergedSecrets;
        return merged.ToJsonString();
    }

    public static string? Secret(string? storedJson, string name)
    {
        if (string.IsNullOrWhiteSpace(storedJson) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (JsonNode.Parse(storedJson) is not JsonObject root || root["secrets"] is not JsonObject secrets)
        {
            return null;
        }

        if (secrets[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
        {
            return text;
        }

        return null;
    }

    public static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, json);
    }

    private static void CollectSecretValues(JsonNode? node, List<string> values)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var property in obj)
        {
            if (string.Equals(property.Key, "secrets", StringComparison.OrdinalIgnoreCase) && property.Value is JsonObject secrets)
            {
                foreach (var secret in secrets)
                {
                    if (secret.Value is JsonValue secretValue && secretValue.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
                    {
                        values.Add(text);
                    }
                }

                continue;
            }

            if (IsSecretName(property.Key) && property.Value is JsonValue named && named.TryGetValue<string>(out var namedText) && !string.IsNullOrEmpty(namedText))
            {
                values.Add(namedText);
            }

            CollectSecretValues(property.Value, values);
        }
    }

    private static JsonNode? RedactNode(JsonNode? node, bool insideProxyRule)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var property in obj)
                {
                    if (!insideProxyRule && IsSecretName(property.Key) && property.Value is JsonValue)
                    {
                        copy[property.Key] = true;
                        continue;
                    }

                    var childIsRule = string.Equals(property.Key, "proxyAllow", StringComparison.OrdinalIgnoreCase);
                    if (childIsRule && property.Value is JsonArray)
                    {
                        copy[property.Key] = RedactProxyArray(property.Value as JsonArray);
                        continue;
                    }

                    copy[property.Key] = RedactNode(property.Value, insideProxyRule);
                }

                return copy;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(RedactNode(item?.DeepClone(), insideProxyRule));
                }

                return items;
            default:
                return node.DeepClone();
        }
    }

    private static JsonArray RedactProxyArray(JsonArray? array)
    {
        var items = new JsonArray();
        if (array == null)
        {
            return items;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject rule)
            {
                continue;
            }

            var copy = new JsonObject();
            foreach (var property in rule)
            {
                // "secret" here is the name of a key in the secrets bag, not the value.
                if (!string.Equals(property.Key, "secret", StringComparison.OrdinalIgnoreCase)
                    && IsSecretName(property.Key)
                    && property.Value is JsonValue)
                {
                    copy[property.Key] = true;
                    continue;
                }

                copy[property.Key] = property.Value?.DeepClone();
            }

            items.Add(copy);
        }

        return items;
    }
}
