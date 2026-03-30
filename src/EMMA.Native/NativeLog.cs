using System.Collections.Generic;

namespace EMMA.Native;

internal enum NativeLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error
}

internal sealed record NativeLogEntry(
    long Sequence,
    DateTimeOffset TimestampUtc,
    NativeLogLevel Level,
    string Category,
    string Message);

internal sealed class NativeLogStore
{
    private readonly Lock _lock = new();
    private readonly int _capacity;
    private readonly Queue<NativeLogEntry> _entries;
    private long _nextSequence;

    public NativeLogStore(int capacity = 2000)
    {
        _capacity = Math.Max(100, capacity);
        _entries = new Queue<NativeLogEntry>(_capacity);
    }

    public bool ConsoleEnabled { get; private set; } = true;
    public NativeLogLevel ConsoleMinLevel { get; private set; } = NativeLogLevel.Information;

    public long LatestSequence
    {
        get
        {
            lock (_lock)
            {
                return _nextSequence;
            }
        }
    }

    public void SetConsoleEnabled(bool enabled)
    {
        lock (_lock)
        {
            ConsoleEnabled = enabled;
        }
    }

    public void SetConsoleMinLevel(NativeLogLevel level)
    {
        lock (_lock)
        {
            ConsoleMinLevel = level;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public NativeLogEntry Write(NativeLogLevel level, string category, string message)
    {
        var safeCategory = string.IsNullOrWhiteSpace(category) ? "native" : category.Trim();
        var safeMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();

        NativeLogEntry entry;
        bool consoleEnabled;
        NativeLogLevel consoleMinLevel;

        lock (_lock)
        {
            _nextSequence++;
            var sequence = _nextSequence;
            entry = new NativeLogEntry(sequence, DateTimeOffset.UtcNow, level, safeCategory, safeMessage);

            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }

            consoleEnabled = ConsoleEnabled;
            consoleMinLevel = ConsoleMinLevel;
        }

        if (consoleEnabled && level >= consoleMinLevel)
        {
            Console.WriteLine($"[EMMA.Native][{entry.TimestampUtc:O}][{entry.Level}][{entry.Category}] {entry.Message}");
        }

        return entry;
    }

    public IReadOnlyList<NativeLogEntry> ReadSince(long afterSequence, int maxItems)
    {
        var take = maxItems <= 0 ? 200 : Math.Min(maxItems, 2000);

        lock (_lock)
        {
            var items = new List<NativeLogEntry>(Math.Min(take, _entries.Count));
            foreach (var entry in _entries)
            {
                if (entry.Sequence <= afterSequence)
                {
                    continue;
                }

                items.Add(entry);
                if (items.Count >= take)
                {
                    break;
                }
            }

            return items;
        }
    }
}
