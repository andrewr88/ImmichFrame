using ImmichFrame.Core.Exceptions;

namespace ImmichFrame.WebApi.Helpers.Config;

/// <summary>
/// Every way a set of settings failed validation - however many there were - refused as one
/// exception.
/// <para>
/// Validation goes on past its first failure because it is not only a check. Validating an account
/// is also what reads its ApiKeyFile into its key, and stored settings that fail validation are run
/// regardless - the admin editor that fixes them is served by the same process - so stopping at the
/// first failure left every account after it, in every configuration validated after it, running
/// with an empty key. It also told the operator about one mistake per restart.
/// </para>
/// <para>
/// <see cref="Exception.InnerException"/> is the first failure, where a single one would be, and the
/// message is every failure's message in the order they were met. <see cref="Failures"/> holds all of
/// them, and is what anything looking for one particular kind of failure has to search: the inner
/// chain only ever reaches the first, so an unreadable key file that was the second thing wrong would
/// be missed there.
/// </para>
/// </summary>
internal sealed class AggregateSettingsNotValidException(IReadOnlyList<Exception> failures)
    : SettingsNotValidException(string.Join(" ", failures.Select(failure => failure.Message)), failures[0])
{
    /// <summary>Every failure, in the order validation met them; the first is the inner exception.</summary>
    public IReadOnlyList<Exception> Failures { get; } = failures;
}
