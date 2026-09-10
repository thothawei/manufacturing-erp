using Microsoft.Extensions.Logging;

namespace Erp.Infrastructure.Tests;

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// 記下寫出去的 log，讓測試能斷言「例外全文有進伺服器 log」
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
}
