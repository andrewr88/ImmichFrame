using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichFrame.Core.Exceptions;

namespace ImmichFrame.WebApi.Helpers.Config;

/// <inheritdoc cref="IConfigDocument"/>
internal sealed class JsonConfigDocument(string _json) : IConfigDocument
{
    internal const string ProfilesKey = "Profiles";

    public IReadOnlyCollection<string> ProfileNames =>
        ProfilesOf(Parse())?.Select(profile => profile.Key).ToList() ?? [];

    public bool HasKey(string name) => Parse().ContainsKey(name);

    public T Bind<T>(string? profileName) where T : IConfigSettable, new()
    {
        // Re-parsed on every call so each profile is merged into a pristine copy of the base
        // document and profiles can never see one another's overrides.
        var root = Parse();

        JsonNode? overrides = null;
        if (!string.IsNullOrEmpty(profileName))
        {
            if (!TryFindProfile(root, profileName, out var declared))
            {
                throw new ProfileNotFoundException($"No configuration profile named '{profileName}' is configured.");
            }

            // Detached from the 'Profiles' node, which is about to be dropped - a JsonNode may only
            // ever have one parent.
            overrides = declared?.DeepClone();
        }

        root.Remove(ProfilesKey);

        if (overrides is JsonObject overrideObject)
        {
            Merge(root, overrideObject);
        }

        try
        {
            return root.Deserialize<T>() ?? throw new SettingsNotValidException("The settings file contained no settings.");
        }
        catch (JsonException ex)
        {
            throw new SettingsNotValidException($"Problem with parsing the settings: {ex.Message}", ex);
        }
    }

    public JsonObject DeclaredOverrides(string? profileName)
    {
        // Freshly parsed, like Bind, so the caller is handed a detached tree it may keep or mutate
        // without the document seeing it.
        var root = Parse();

        if (string.IsNullOrEmpty(profileName))
        {
            root.Remove(ProfilesKey);
            return root;
        }

        if (!TryFindProfile(root, profileName, out var declared))
        {
            throw new ProfileNotFoundException($"No configuration profile named '{profileName}' is configured.");
        }

        // 'empty: null' is a declared profile that overrides nothing, not a missing one.
        return declared?.DeepClone() as JsonObject ?? [];
    }

    private JsonObject Parse()
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(_json);
        }
        catch (JsonException ex)
        {
            throw new SettingsNotValidException($"Problem with parsing the settings: {ex.Message}", ex);
        }

        return root as JsonObject
               ?? throw new SettingsNotValidException("The settings file must contain a JSON object at its root.");
    }

    private static JsonObject? ProfilesOf(JsonObject root)
    {
        if (!root.TryGetPropertyValue(ProfilesKey, out var profiles) || profiles is null) return null;

        return profiles as JsonObject
               ?? throw new SettingsNotValidException($"'{ProfilesKey}' must be an object mapping profile names to their settings.");
    }

    private static bool TryFindProfile(JsonObject root, string profileName, out JsonNode? overrides)
    {
        overrides = null;

        var profiles = ProfilesOf(root);
        if (profiles is null) return false;

        foreach (var (name, declared) in profiles)
        {
            if (!string.Equals(name, profileName, StringComparison.OrdinalIgnoreCase)) continue;

            overrides = declared;
            return true;
        }

        return false;
    }

    private static void Merge(JsonObject target, JsonObject overrides)
    {
        foreach (var (key, value) in overrides)
        {
            if (target.TryGetPropertyValue(key, out var existing)
                && existing is JsonObject existingObject
                && value is JsonObject overrideObject)
            {
                // Both sides are objects - 'General', say - so merge key by key and let the profile
                // name only the settings it actually changes.
                Merge(existingObject, overrideObject);
                continue;
            }

            // Everything else replaces the base value outright, lists such as 'Accounts' included:
            // there is no unambiguous way to merge two lists.
            target[key] = value?.DeepClone();
        }
    }
}
