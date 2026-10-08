using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Tests.Services;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps everything it is given, for tests that assert on what
/// reaches the log - and, for secrets, on what never does.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
}

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception)
{
    // Everything a sink could write for this entry: the console writes the exception after the
    // message, so a "never logged" assertion that only searched the message would miss it.
    public string Text => Exception is null ? Message : $"{Message} {Exception}";
}
