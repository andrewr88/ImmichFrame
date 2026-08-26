using ImmichFrame.Core.Exceptions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace ImmichFrame.WebApi.Helpers.Config;

/// <inheritdoc cref="IConfigDocument"/>
internal sealed class YamlConfigDocument(string _yaml) : IConfigDocument
{
    internal const string ProfilesKey = "Profiles";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public IReadOnlyCollection<string> ProfileNames =>
        ProfilesOf(Parse())?.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty).ToList() ?? [];

    public bool HasKey(string name) => TryFindChild(Parse(), name, StringComparison.Ordinal, out _, out _);

    public T Bind<T>(string? profileName) where T : IConfigSettable, new()
    {
        // Re-parsed on every call so each profile is merged into a pristine copy of the base
        // document and profiles can never see one another's overrides.
        var root = Parse();

        YamlNode? overrides = null;
        if (!string.IsNullOrEmpty(profileName))
        {
            if (!TryFindProfile(root, profileName, out overrides))
            {
                throw new ProfileNotFoundException($"No configuration profile named '{profileName}' is configured.");
            }
        }

        if (TryFindChild(root, ProfilesKey, StringComparison.Ordinal, out var profilesKey, out _))
        {
            root.Children.Remove(profilesKey!);
        }

        if (overrides is YamlMappingNode overrideMapping)
        {
            Merge(root, overrideMapping);
        }

        try
        {
            return Deserializer.Deserialize<T>(Serialize(root))
                   ?? throw new SettingsNotValidException("The settings file contained no settings.");
        }
        catch (YamlException ex)
        {
            throw new SettingsNotValidException($"Problem with parsing the settings: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes the merged node tree back out so the ordinary deserializer, with its aliases and
    /// converters, can bind it - the representation model has no binding of its own.
    /// </summary>
    private static string Serialize(YamlMappingNode root)
    {
        using var writer = new StringWriter();
        new YamlStream(new YamlDocument(root)).Save(writer);
        return writer.ToString();
    }

    private YamlMappingNode Parse()
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(_yaml));
        }
        catch (YamlException ex)
        {
            throw new SettingsNotValidException($"Problem with parsing the settings: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0)
        {
            throw new SettingsNotValidException("The settings file is empty.");
        }

        return stream.Documents[0].RootNode as YamlMappingNode
               ?? throw new SettingsNotValidException("The settings file must contain a mapping at its root.");
    }

    private static YamlMappingNode? ProfilesOf(YamlMappingNode root)
    {
        if (!TryFindChild(root, ProfilesKey, StringComparison.Ordinal, out _, out var profiles)) return null;
        if (profiles is null or YamlScalarNode { Value: null or "" }) return null;

        return profiles as YamlMappingNode
               ?? throw new SettingsNotValidException($"'{ProfilesKey}' must be a mapping of profile names to their settings.");
    }

    private static bool TryFindProfile(YamlMappingNode root, string profileName, out YamlNode? overrides)
    {
        overrides = null;

        var profiles = ProfilesOf(root);
        return profiles is not null
               && TryFindChild(profiles, profileName, StringComparison.OrdinalIgnoreCase, out _, out overrides);
    }

    private static void Merge(YamlMappingNode target, YamlMappingNode overrides)
    {
        foreach (var (key, value) in overrides.Children.ToList())
        {
            if (key is not YamlScalarNode { Value: { } name }) continue;

            var found = TryFindChild(target, name, StringComparison.Ordinal, out var existingKey, out var existing);

            if (found && existing is YamlMappingNode existingMapping && value is YamlMappingNode overrideMapping)
            {
                // Both sides are mappings - 'General', say - so merge key by key and let the profile
                // name only the settings it actually changes.
                Merge(existingMapping, overrideMapping);
                continue;
            }

            // Everything else replaces the base value outright, sequences such as 'Accounts'
            // included: there is no unambiguous way to merge two sequences.
            if (found) target.Children.Remove(existingKey!);
            target.Children.Add(key, value);
        }
    }

    private static bool TryFindChild(YamlMappingNode mapping, string name, StringComparison comparison,
        out YamlNode? key, out YamlNode? value)
    {
        // Matched by scalar value rather than by node equality, which also takes tags and anchors
        // into account and would make an explicitly tagged key silently miss.
        foreach (var child in mapping.Children)
        {
            if (child.Key is not YamlScalarNode scalar || !string.Equals(scalar.Value, name, comparison)) continue;

            key = child.Key;
            value = child.Value;
            return true;
        }

        key = null;
        value = null;
        return false;
    }
}
