using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BigMission.Shared.Tests.TestHelpers;

/// <summary>One entry a component wrote to its logger.</summary>
internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// An <see cref="ILoggerFactory"/> that keeps everything written to it.
/// </summary>
/// <remarks>
/// Preferred over a mocked <see cref="ILogger"/> here because the code under test logs through the
/// extension methods, so a mock has to be set up against the generic <c>Log</c> overload and its
/// state object - which reads as a puzzle at the assertion site. What these tests care about is the
/// level and the rendered message, which is exactly what this records.
/// </remarks>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<LogEntry> entries = new();

    /// <summary>Everything logged so far, oldest first.</summary>
    public IReadOnlyList<LogEntry> Entries => [.. entries];

    /// <summary>The entries whose message contains <paramref name="substring"/>, oldest first.</summary>
    public IReadOnlyList<LogEntry> Matching(string substring)
        => [.. entries.Where(e => e.Message.Contains(substring, StringComparison.Ordinal))];

    /// <summary>
    /// Waits until at least <paramref name="count"/> entries match <paramref name="substring"/>.
    /// </summary>
    /// <returns>The matching entries, or whatever had arrived when the wait timed out.</returns>
    public async Task<IReadOnlyList<LogEntry>> WaitForAsync(string substring, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var matches = Matching(substring);
            if (matches.Count >= count)
            {
                return matches;
            }

            await Task.Delay(10);
        }

        return Matching(substring);
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(entries);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
