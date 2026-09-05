using System.Text.Json.Nodes;
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

    public JsonObject DeclaredOverrides(string? profileName)
    {
        // Freshly parsed, like Bind, so the caller is handed a detached tree it may keep or mutate
        // without the document seeing it.
        var root = Parse();

        if (string.IsNullOrEmpty(profileName))
        {
            if (TryFindChild(root, ProfilesKey, StringComparison.Ordinal, out var profilesKey, out _))
            {
                root.Children.Remove(profilesKey!);
            }

            return ToJson(root);
        }

        if (!TryFindProfile(root, profileName, out var overrides))
        {
            throw new ProfileNotFoundException($"No configuration profile named '{profileName}' is configured.");
        }

        // 'empty:' is a declared profile that overrides nothing, not a missing one.
        return overrides is YamlMappingNode mapping ? ToJson(mapping) : [];
    }

    /// <summary>
    /// Projects a YAML node onto the JSON node model, so that one caller can inspect either format.
    /// <para>
    /// Every scalar becomes a JSON string, including numbers and booleans: YAML's representation
    /// model carries no resolved type, and guessing one here would turn a deliberately quoted
    /// '10' into a number. <see cref="IConfigDocument.DeclaredOverrides"/> documents that
    /// consequence - this projection answers which keys are declared, not what they bind to.
    /// </para>
    /// </summary>
    private static JsonObject ToJson(YamlMappingNode mapping)
    {
        RefuseAnchors(mapping);

        var result = new JsonObject();

        foreach (var (key, value) in mapping.Children)
        {
            if (key is not YamlScalarNode { Value: { } name }) continue;

            result[name] = ToJson(value);
        }

        return result;
    }

    private static JsonNode? ToJson(YamlNode node)
    {
        RefuseAnchors(node);

        return node switch
        {
            YamlMappingNode mapping => ToJson(mapping),
            YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJson).ToArray()),
            // A plain empty, '~' or 'null' scalar is YAML's null; quoted, the same characters are a
            // string the user meant to write.
            YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" or "~" or "null" } => null,
            YamlScalarNode { Value: { } value } => JsonValue.Create(value),
            _ => null
        };
    }

    /// <summary>
    /// Refuses a document that uses YAML anchors, rather than quietly flattening one.
    /// <para>
    /// <see cref="YamlStream"/> resolves an alias to the very node the anchor named, so an aliased
    /// mapping is indistinguishable here from one written out longhand. Projecting it would report
    /// every inherited value as a key the profile declares, and saving would then write them all
    /// literally: the anchor vanishes and the profile silently becomes an explicit override of
    /// settings it used to share. That is the same drift the declared-key machinery exists to
    /// prevent, so the editor says it cannot edit this file instead of mangling it. Losing comments
    /// was an accepted trade; losing the structure of the configuration is not.
    /// </para>
    /// <para>
    /// Checked on the aliased node rather than on the document, because the node an alias resolves to
    /// carries the anchor name wherever it appears - so both the definition and every use are caught,
    /// whichever subtree is being projected.
    /// </para>
    /// </summary>
    private static void RefuseAnchors(YamlNode node)
    {
        if (node.Anchor.IsEmpty) return;

        throw new SettingsNotValidException(
            $"The settings file uses the YAML anchor '&{node.Anchor}'. The configuration editor cannot " +
            "read or write a file that shares nodes between sections, because it cannot tell an aliased " +
            "value from one the section declares itself. Write the anchored values out in full to edit " +
            "this file here.");
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
