using System.Diagnostics.CodeAnalysis;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.WebApi.Helpers.Config;

/// <summary>
/// The set of configurations ImmichFrame was started with: one default plus any number of named
/// profiles. Every profile is a complete <see cref="IServerSettings"/> in its own right - a profile
/// declared in the settings file only names the values it changes, but the catalog hands out the
/// fully merged result.
/// </summary>
public interface IConfigCatalog
{
    /// <summary>
    /// The configuration a client gets when it does not ask for a particular profile.
    /// </summary>
    IServerSettings Default { get; }

    /// <summary>
    /// The names of the configured profiles, excluding <see cref="Default"/>.
    /// </summary>
    IReadOnlyCollection<string> ProfileNames { get; }

    /// <summary>
    /// Looks a profile up by name, case-insensitively. A null, empty or
    /// <see cref="ConfigCatalog.DefaultProfileName"/> name resolves to <see cref="Default"/>.
    /// </summary>
    bool TryGet(string? name, [NotNullWhen(true)] out IServerSettings? settings);

    /// <inheritdoc cref="TryGet"/>
    /// <exception cref="ProfileNotFoundException">No profile of that name is configured.</exception>
    IServerSettings Get(string? name);
}
