using System.Text;
using System.Text.Json;

const string Usage = """
    Usage:
      LiveLog <game dir> [--changes]   Follow the newest GameHookLogs file and print each tick.
                                      --changes prints a tick only when held/menu changed.
    """;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--changes"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

string logDir = Path.Combine(args[0], "GameHookLogs");
bool changesOnly = args.Length == 2;

// file already existing at startup -> read silently up to end
// later files print in full
string? startupNewest = LogFollower.FindNewest(logDir);
LogFollower? follower = null;
Console.WriteLine($"Watching {logDir} (Ctrl+C to stop)");

while (true)
{
    string? newest = LogFollower.FindNewest(logDir);
    if (newest != null && newest != follower?.Path)
    {
        follower?.Dispose();
        follower = new LogFollower(newest, changesOnly, catchUp: newest == startupNewest);
        Console.WriteLine($"=== {Path.GetFileName(newest)} ===");
    }

    // Drain everything available, then check for a newer file every ~250 ms.
    for (int i = 0; i < 50 && follower != null; i++)
    {
        if (!follower.Poll())
            Thread.Sleep(5);
    }
    if (follower == null)
        Thread.Sleep(250);
}

/// <summary>Reads one log file as it grows and prints decoded lines.</summary>
sealed class LogFollower : IDisposable
{
    public string Path { get; }

    readonly FileStream _stream;
    readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    readonly byte[] _bytes = new byte[64 * 1024];
    readonly char[] _chars = new char[64 * 1024];
    readonly StringBuilder _partial = new();
    readonly bool _changesOnly;
    bool _catchingUp;

    long _freq;
    long? _firstTs;
    string[] _names = [];
    uint? _lastTick;
    ulong? _lastHeld;
    bool? _lastMenu;

    public LogFollower(string path, bool changesOnly, bool catchUp)
    {
        Path = path;
        _changesOnly = changesOnly;
        _catchingUp = catchUp;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public static string? FindNewest(string dir) =>
        Directory.Exists(dir)
            // names are gamehook-yyyyMMdd-HHmmss, so ordinal order is time order
            ? Directory.GetFiles(dir, "gamehook-*.jsonl").Max(StringComparer.Ordinal)
            : null;

    /// <summary>Processes whatever has been appended, returns false at eof.</summary>
    public bool Poll()
    {
        int read = _stream.Read(_bytes, 0, _bytes.Length);
        if (read == 0)
        {
            if (_catchingUp)
            {
                _catchingUp = false;
                Console.WriteLine($"(skipped earlier ticks, live from tick {_lastTick})");
            }
            return false;
        }

        // The decoder keeps a multi-byte character split across reads for the next call.
        int count = _decoder.GetChars(_bytes, 0, read, _chars, 0);
        for (int i = 0; i < count; i++)
        {
            if (_chars[i] == '\n')
            {
                HandleLine(_partial.ToString());
                _partial.Clear();
            }
            else
            {
                _partial.Append(_chars[i]);
            }
        }
        return true;
    }

    void HandleLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            switch (root.GetProperty("ev").GetString())
            {
                case "start":
                    _freq = root.GetProperty("freq").GetInt64();
                    Print($"start  (Stopwatch freq {_freq})");
                    return;
                case "triggers":
                    _names = root.GetProperty("names").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    Print($"triggers  {_names.Length} names, {root.GetProperty("dropped").GetInt32()} dropped");
                    return;
                case "tick":
                    HandleTick(root);
                    return;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // fall through: show lines we do not understand instead of hiding them
        }
        Print($"?  {line}");
    }

    void HandleTick(JsonElement root)
    {
        long ts = root.GetProperty("ts").GetInt64();
        uint tick = root.GetProperty("tick").GetUInt32();
        ulong held = root.GetProperty("held").GetUInt64();
        bool menu = root.GetProperty("menu").GetInt32() != 0;

        _firstTs ??= ts;
        // The counter goes up by exactly one per tick, so a jump means rows were lost.
        long gap = _lastTick is uint last ? (long)tick - last - 1 : 0;
        bool changed = held != _lastHeld || menu != _lastMenu;
        _lastTick = tick;
        _lastHeld = held;
        _lastMenu = menu;

        if (_changesOnly && !changed && gap == 0)
            return;

        double seconds = _freq > 0 ? (double)(ts - _firstTs.Value) / _freq : 0;
        string gapNote = gap != 0 ? $"  [GAP {gap}]" : "";
        Print($"{tick,8} {seconds,9:F3}s {(menu ? "MENU" : "    ")}  {Decode(held)}{gapNote}");
    }

    string Decode(ulong held)
    {
        if (held == 0)
            return "-";
        var parts = new List<string>();
        for (int i = 0; i < 64; i++)
        {
            if ((held >> i & 1) != 0)
                parts.Add(i < _names.Length ? _names[i] : $"bit{i}");
        }
        return string.Join(' ', parts);
    }

    void Print(string text)
    {
        if (!_catchingUp)
            Console.WriteLine(text);
    }

    public void Dispose() => _stream.Dispose();
}
