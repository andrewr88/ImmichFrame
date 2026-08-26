
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
}
