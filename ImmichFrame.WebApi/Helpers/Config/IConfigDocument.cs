using System.Text.Json.Nodes;

namespace ImmichFrame.WebApi.Helpers.Config;

/// <summary>
/// A settings file that has been parsed but not yet bound to a settings class.
/// <para>
/// Profile overrides are merged at the document level rather than on the bound object because the
/// settings classes carry non-nullable properties with baked-in defaults - once bound, an omitted
/// <c>ShowClock</c> is indistinguishable from an explicit <c>ShowClock: false</c>. Merging while the
/// settings are still a document keeps "the profile said so" and "the profile said nothing" apart,
/// and lets a single settings class serve both the default configuration and every profile.
/// </para>
/// </summary>
internal interface IConfigDocument
{
    /// <summary>
    /// The names of the profiles declared in the document, in the order they appear.
    /// </summary>
    IReadOnlyCollection<string> ProfileNames { get; }

    /// <summary>
    /// Whether the document declares a top-level key of the given name.
    /// </summary>
    bool HasKey(string name);

    /// <summary>
    /// Binds the document to <typeparamref name="T"/>. A null or empty <paramref name="profileName"/>
    /// binds the base configuration; otherwise that profile is merged over the base first.
    /// </summary>
    T Bind<T>(string? profileName) where T : IConfigSettable, new();

    /// <summary>
    /// What a profile actually declares, as opposed to what it resolves to. A null or empty
    /// <paramref name="profileName"/> returns the base document without its <c>Profiles</c> section;
    /// otherwise it returns that profile's own subtree, empty when the profile declares nothing.
    /// <para>
    /// This is the half of the document <see cref="Bind{T}"/> deliberately throws away, and the
    /// admin editor needs both: which keys a profile overrides is what tells an inherited value from
    /// an explicit one on screen, and it is what lets a profile be written back without turning
    /// every value it inherits into an override of its own.
    /// </para>
    /// <para>
    /// A <see cref="JsonObject"/> rather than each format's own node type, so that one caller can
    /// read either. Be clear about the cost: scalars from a YAML document arrive as JSON
    /// <em>strings</em> whatever they would bind to, because YAML's representation model does not
    /// resolve types. Read this to find out which keys are declared and to read declared strings -
    /// never to bind settings, and never to write a file from.
    /// </para>
    /// </summary>
    /// <exception cref="Core.Exceptions.ProfileNotFoundException">No profile of that name is declared.</exception>
    JsonObject DeclaredOverrides(string? profileName);
}
