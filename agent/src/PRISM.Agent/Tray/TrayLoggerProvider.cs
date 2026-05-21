using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

// Avoid ambiguity with PRISM.Contracts.LogLevel
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PRISM.Agent.Tray;

/// <summary>
/// In-process log provider that feeds the <see cref="LogsForm"/> live log viewer.
/// Thread-safe: log calls from any thread enqueue a formatted line and fire
/// <see cref="OnLogLine"/>. <see cref="LogsForm"/> marshals to the UI thread.
/// </summary>
public sealed class TrayLoggerProvider : ILoggerProvider
{
    const int MaxLines = 2000;

    readonly ConcurrentQueue<string> _buffer = new();

    /// <summary>Fired on the caller's thread whenever a new line is logged.</summary>
    public event Action<string>? OnLogLine;

    public ILogger CreateLogger(string categoryName) =>
        new TrayLogger(categoryName, this);

    public IReadOnlyCollection<string> GetSnapshot() => _buffer.ToArray();

    internal void Push(string line)
    {
        _buffer.Enqueue(line);
        // Trim old lines to cap memory usage.
        while (_buffer.Count > MaxLines)
            _buffer.TryDequeue(out _);
        OnLogLine?.Invoke(line);
    }

    public void Dispose() { }

    // ------------------------------------------------------------------

    sealed class TrayLogger : ILogger
    {
        readonly string _categoryName;
        readonly TrayLoggerProvider _provider;

        public TrayLogger(string categoryName, TrayLoggerProvider provider)
        {
            _categoryName = categoryName;
            _provider     = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MelLogLevel level) => level >= MelLogLevel.Information;

        public void Log<TState>(
            MelLogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;

            var levelTag = level switch
            {
                MelLogLevel.Information => "INF",
                MelLogLevel.Warning     => "WRN",
                MelLogLevel.Error       => "ERR",
                MelLogLevel.Critical    => "CRT",
                MelLogLevel.Debug       => "DBG",
                MelLogLevel.Trace       => "TRC",
                _                       => "???",
            };

            // Shorten the category to just the last segment for readability.
            var cat = _categoryName.Contains('.')
                ? _categoryName[(_categoryName.LastIndexOf('.') + 1)..]
                : _categoryName;

            var line = $"[{DateTime.Now:HH:mm:ss}] {levelTag} [{cat}] {formatter(state, exception)}";
            if (exception != null)
                line += Environment.NewLine + "  " + exception;

            _provider.Push(line);
        }
    }
}
