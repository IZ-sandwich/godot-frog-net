using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;

namespace MonkeNet.Shared;

[GlobalClass]
public partial class MonkeLogger : Node
{
    public static MonkeLogger Instance { get; private set; }

    /// <summary>
    /// Gates <see cref="Debug"/> output. Toggle in the inspector on the autoload to
    /// enable/disable verbose logs without recompiling. Default off so production runs
    /// stay quiet — Info/Warn/Error always print.
    /// </summary>
    [Export] public bool DebugEnabled { get; set; } = false;

    // File sink. The StreamWriter is OWNED by the background writer thread; the
    // main (Godot) thread only formats messages into pooled char buffers and
    // enqueues them via a lock-free ConcurrentQueue.
    //
    // History — a prior synchronous + AutoFlush=true design caused ~1 s periodic
    // physics-tick stalls on the client under heavy DEBUG output (~16k lines/s,
    // ~3 MB/s sustained). Two effects compounded: (1) every `WriteLine` was a
    // syscall on the main thread; (2) each `$"…"` interpolation allocated a fresh
    // string on the main thread, driving Gen2 GC pauses up to a full second on
    // Workstation GC. A first async-wrapper attempt (BlockingCollection-based)
    // moved the I/O off the main thread but kept the per-call string allocation
    // AND added SemaphoreSlim contention on every Debug(); the stalls persisted.
    //
    // The current design eliminates both:
    //   - Producer (main thread) writes directly into a char[] rented from
    //     ArrayPool<char>.Shared via a custom InterpolatedStringHandler. No
    //     intermediate `string` is ever allocated for the message body. Format
    //     specifiers (`{X:F3}`, etc.) go through ISpanFormattable.TryFormat on
    //     primitives — also allocation-free.
    //   - The queue is a lock-free ConcurrentQueue; producer enqueues never
    //     touch a SemaphoreSlim or wait on anything. An AutoResetEvent wakes
    //     the writer thread; queue depth is tracked with an Interlocked counter
    //     so producer can drop instead of blocking when the cap is exceeded.
    //   - Timestamp + identity fields are captured cheaply on the producer (a
    //     DateTime struct, an int, a short already-cached string) and FORMATTED
    //     on the writer thread — so the prefix doesn't allocate on the hot path
    //     either.
    private static StreamWriter _file;
    private static readonly object _fileLock = new();
    private static string _filePath;

    /// <summary>Absolute path of the log file this process is writing to, or null
    /// if file logging is disabled / failed to open. Surfaced so a test harness
    /// can copy the live log out at run end without having to rediscover the path.</summary>
    public static string FilePath => _filePath;

    private static ConcurrentQueue<PooledLogEntry> _queue;
    private static AutoResetEvent _signal;
    private static Thread _writer;
    private static volatile bool _stopRequested;
    private static long _droppedCount;
    private static int _queueSize;

    // Soft cap on outstanding queued entries. Sized for ~5 s of worst-case
    // DEBUG-spam (16 k/s × 5 s = 80 k entries). If exceeded, the producer
    // returns its rented buffer to the pool and increments _droppedCount —
    // dropping is strictly better than blocking the physics tick.
    private const int QueueCap = 100_000;

    // How long the writer thread sleeps when the queue is empty. Bounds how
    // long it takes for a non-flushed line to appear after activity stops.
    private const int WriterIdleMs = 100;

    // Per-entry payload. Owns a char[] rented from ArrayPool until the writer
    // thread returns it. The PeerId / TokSuffix / Level fields are captured
    // cheaply on the producer but formatted into the file on the writer.
    private readonly struct PooledLogEntry
    {
        public readonly DateTime Timestamp;
        public readonly int PeerId;
        public readonly string TokSuffix;  // already-short (4 chars or "----")
        public readonly string Level;      // interned constant ("DEBUG", etc.)
        public readonly char[] Message;    // rented from ArrayPool<char>.Shared
        public readonly int MessageLength;
        public readonly bool FlushAfter;

        public PooledLogEntry(DateTime ts, int peerId, string tok, string level, char[] msg, int len, bool flush)
        {
            Timestamp = ts; PeerId = peerId; TokSuffix = tok; Level = level;
            Message = msg; MessageLength = len; FlushAfter = flush;
        }
    }

    public override void _EnterTree()
    {
        Instance = this;
        OpenLogFile();
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
        CloseLogFile();
    }

    private static void OpenLogFile()
    {
        try
        {
            string logDir = ProjectSettings.GlobalizePath("user://logs");
            Directory.CreateDirectory(logDir);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            _filePath = Path.Combine(logDir, $"monke-net_{stamp}_pid{System.Environment.ProcessId}.log");
            // AutoFlush=false: the writer thread does explicit Flush() calls.
            // Letting StreamWriter's 4 KB userland buffer batch saves ~20× the
            // WriteFile syscalls under heavy DEBUG load.
            _file = new StreamWriter(new FileStream(_filePath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read))
            {
                AutoFlush = false,
            };
            _queue = new ConcurrentQueue<PooledLogEntry>();
            _signal = new AutoResetEvent(false);
            _stopRequested = false;
            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "MonkeLogger.Writer",
            };
            _writer.Start();
            GD.Print($"[MonkeLogger] writing full log to {_filePath}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MonkeLogger] failed to open log file: {ex.Message}");
            _file = null;
            _queue = null;
        }
    }

    private static void WriterLoop()
    {
        // Reusable scratch buffer for the prefix; never resized because the
        // prefix is bounded (~80 chars). Lives for the lifetime of the
        // writer thread, so the only allocation per entry is the Enqueue/
        // Dequeue plumbing inside ConcurrentQueue.
        char[] prefixBuf = new char[96];

        while (!_stopRequested || !_queue.IsEmpty)
        {
            bool wrote = false;
            while (_queue.TryDequeue(out var entry))
            {
                Interlocked.Decrement(ref _queueSize);
                try
                {
                    int prefixLen = FormatPrefix(prefixBuf, entry.Timestamp, entry.PeerId, entry.TokSuffix, entry.Level);
                    _file.Write(prefixBuf, 0, prefixLen);
                    _file.Write(entry.Message, 0, entry.MessageLength);
                    _file.Write(System.Environment.NewLine);
                    wrote = true;
                }
                catch
                {
                    // Swallow per-line errors — losing one DEBUG line is far better than
                    // tearing down the writer thread.
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(entry.Message);
                }

                if (entry.FlushAfter)
                {
                    try { _file.Flush(); } catch { }
                }
            }
            if (wrote)
            {
                try { _file.Flush(); } catch { }
            }
            // Sleep until producer signals OR the idle interval ticks. The
            // idle Flush above ensures durability even when the producer is
            // bursting without using FlushAfter.
            _signal.WaitOne(WriterIdleMs);
        }

        // Drain on shutdown — caught any entries enqueued between the queue
        // check and the loop exit.
        while (_queue.TryDequeue(out var entry))
        {
            try
            {
                int prefixLen = FormatPrefix(prefixBuf, entry.Timestamp, entry.PeerId, entry.TokSuffix, entry.Level);
                _file.Write(prefixBuf, 0, prefixLen);
                _file.Write(entry.Message, 0, entry.MessageLength);
                _file.Write(System.Environment.NewLine);
            }
            catch { }
            finally
            {
                ArrayPool<char>.Shared.Return(entry.Message);
            }
        }
        try { _file?.Flush(); } catch { }
    }

    // Format "[YYYY-MM-DDTHH:MM:SS.fff] [peerId]\t\t[tok:XXXX] [LEVEL]\t"
    // directly into `buf`. Returns the number of chars written. Allocation
    // free — uses ISpanFormattable on each numeric component.
    private static int FormatPrefix(char[] buf, DateTime ts, int peerId, string tok, string level)
    {
        int p = 0;
        buf[p++] = '[';
        // yyyy-MM-ddTHH:mm:ss.fff via DateTime.TryFormat (ISpanFormattable, no alloc)
        if (ts.TryFormat(buf.AsSpan(p), out int written, "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))
            p += written;
        buf[p++] = ']';
        buf[p++] = ' ';
        buf[p++] = '[';
        if (peerId.TryFormat(buf.AsSpan(p), out written, ReadOnlySpan<char>.Empty, CultureInfo.InvariantCulture))
            p += written;
        buf[p++] = ']';
        buf[p++] = '\t';
        buf[p++] = '\t';
        buf[p++] = '[';
        buf[p++] = 't'; buf[p++] = 'o'; buf[p++] = 'k'; buf[p++] = ':';
        tok.AsSpan().CopyTo(buf.AsSpan(p));
        p += tok.Length;
        buf[p++] = ']';
        buf[p++] = ' ';
        buf[p++] = '[';
        level.AsSpan().CopyTo(buf.AsSpan(p));
        p += level.Length;
        buf[p++] = ']';
        buf[p++] = '\t';
        return p;
    }

    private static void CloseLogFile()
    {
        // Signal the writer to drain and exit; join it with a generous window
        // so even a heavily-logged teardown lands on disk.
        _stopRequested = true;
        try { _signal?.Set(); } catch { }
        try { _writer?.Join(TimeSpan.FromSeconds(5)); } catch { }
        lock (_fileLock)
        {
            try { _file?.Flush(); } catch { }
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
        long dropped = Interlocked.Read(ref _droppedCount);
        if (dropped > 0)
            GD.PrintErr($"[MonkeLogger] {dropped} log lines dropped due to queue overflow");
    }

    // Set by the client when it receives a session token; cleared on disconnect.
    // Server leaves this null — server-side logs embed the token per-message instead.
    public static string CurrentToken { get; set; }

    public static void Info(string message) => LogPrebuilt("INFO", message, flushAfter: true);
    public static void Warn(string message) => LogPrebuilt("WARN", message, flushAfter: true);
    public static void Error(string message) => LogPrebuilt("ERROR", message, flushAfter: true);

    /// <summary>Fast static-property check that mirrors the autoload's
    /// <see cref="DebugEnabled"/> toggle. Safe when the autoload isn't in the
    /// tree (returns false). Used both by the interpolated-string handler to
    /// short-circuit placeholder evaluation and by callers that want to gate
    /// expensive log message construction by hand.</summary>
    public static bool IsDebugEnabled => Instance != null && Instance.DebugEnabled;

    /// <summary>
    /// Emits a debug-level log line. The interpolated-string overload below is
    /// the one the compiler picks for any <c>Debug($"...{x}...")</c> call site;
    /// this string-only overload exists for the rare callers that pass a
    /// pre-built or constant string. Both bail out when
    /// <see cref="DebugEnabled"/> is false on the autoload.
    /// </summary>
    public static void Debug(string message)
    {
        if (!IsDebugEnabled) return;
        LogPrebuilt("DEBUG", message, flushAfter: false);
    }

    /// <summary>
    /// Interpolated-string overload of <see cref="Debug(string)"/>. The handler
    /// writes the message into a pooled char[]; we transfer ownership to a
    /// PooledLogEntry and enqueue — no managed string is ever allocated for
    /// the message on the hot path.
    /// </summary>
    public static void Debug([InterpolatedStringHandlerArgument] MonkeLoggerDebugHandler handler)
    {
        if (!IsDebugEnabled) return;
        var (buf, len) = handler.Take();
        if (buf == null) return;
        EnqueuePooled(buf, len, "DEBUG", flushAfter: false);
    }

    private static int _markCounter = 0;

    /// <summary>
    /// Emits a distinctive [MARK] line so a user can drop a needle into the log and
    /// later jump back to that point when reconstructing a session. Always prints
    /// regardless of <see cref="DebugEnabled"/> — the whole point is being findable.
    /// </summary>
    public static void Mark(int tick, string label = null)
    {
        int id = Interlocked.Increment(ref _markCounter);
        string suffix = string.IsNullOrEmpty(label) ? "" : $" label='{label}'";
        LogPrebuilt("MARK", $"#{id} tick={tick}{suffix}", flushAfter: true);
    }

    // Pre-built-string path used by Info/Warn/Error/Mark and by the
    // string-only Debug overload. Copies into a pooled buffer so the
    // writer thread can use the same code path as for handler-built entries.
    private static void LogPrebuilt(string level, string message, bool flushAfter)
    {
        var buf = ArrayPool<char>.Shared.Rent(message.Length);
        message.AsSpan().CopyTo(buf);
        EnqueuePooled(buf, message.Length, level, flushAfter);

        // Editor/console sink — DEBUG is intentionally suppressed here (high
        // volume + the editor's overflow truncation). The file sink already
        // captured it above.
        if (level == "DEBUG") return;
        // Build a one-shot line for the console; this allocates, but Info/
        // Warn/Error are low-frequency so it's not on the hot path.
        string consoleLine = $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}] [{PeerIdSafe()}]\t\t[tok:{TokSuffix()}] [{level}]\t{message}";
        if (level == "ERROR")
            GD.PrintErr(consoleLine);
        else
            GD.Print(consoleLine);
    }

    private static void EnqueuePooled(char[] buf, int len, string level, bool flushAfter)
    {
        var q = _queue;
        if (q == null) { ArrayPool<char>.Shared.Return(buf); return; }
        if (_stopRequested) { ArrayPool<char>.Shared.Return(buf); return; }

        if (Interlocked.Increment(ref _queueSize) > QueueCap)
        {
            Interlocked.Decrement(ref _queueSize);
            Interlocked.Increment(ref _droppedCount);
            ArrayPool<char>.Shared.Return(buf);
            return;
        }
        q.Enqueue(new PooledLogEntry(DateTime.Now, PeerIdSafe(), TokSuffix(), level, buf, len, flushAfter));
        try { _signal?.Set(); } catch { }
    }

    private static int PeerIdSafe()
    {
        try
        {
            var peer = Instance?.Multiplayer?.MultiplayerPeer;
            if (peer != null && peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected)
                return Instance.Multiplayer.GetUniqueId();
        }
        catch { }
        return 0;
    }

    private static string TokSuffix()
    {
        var t = CurrentToken;
        return t != null && t.Length >= 4 ? t[^4..] : "----";
    }

    /// <summary>Custom InterpolatedStringHandler that writes directly into a
    /// char[] rented from <see cref="ArrayPool{T}.Shared"/>. The runtime
    /// constructor checks <see cref="MonkeLogger.IsDebugEnabled"/> FIRST and
    /// reports the answer via the <c>out bool isEnabled</c>; when it's false
    /// the compiler skips every <c>AppendLiteral</c>/<c>AppendFormatted</c>
    /// call entirely, so placeholder expressions are never evaluated and the
    /// disabled-path cost is a single bool comparison.
    /// <para>
    /// When enabled, <see cref="AppendFormatted{T}(T)"/> dispatches through
    /// <see cref="ISpanFormattable"/> for value types (int/float/double/
    /// DateTime/Guid/...) which writes directly into the rented span with
    /// zero allocation. Types that don't implement it fall back to
    /// <c>value.ToString()</c> (the regrettable case) — for our logs this
    /// only matters for things like Godot's <c>Vector3</c>, and most of our
    /// call sites format each component individually (<c>{x:F3}</c>) so the
    /// component formatting stays alloc-free.
    /// </para>
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct MonkeLoggerDebugHandler
    {
        private char[] _buffer;
        private int _pos;
        private readonly bool _enabled;

        public MonkeLoggerDebugHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            isEnabled = MonkeLogger.IsDebugEnabled;
            _enabled = isEnabled;
            if (isEnabled)
            {
                int initialCap = Math.Max(128, literalLength + formattedCount * 16);
                _buffer = ArrayPool<char>.Shared.Rent(initialCap);
                _pos = 0;
            }
            else
            {
                _buffer = null;
                _pos = 0;
            }
        }

        public void AppendLiteral(string value)
        {
            if (!_enabled || string.IsNullOrEmpty(value)) return;
            EnsureRemaining(value.Length);
            value.AsSpan().CopyTo(_buffer.AsSpan(_pos));
            _pos += value.Length;
        }

        public void AppendFormatted<T>(T value)
        {
            if (!_enabled) return;
            if (value is ISpanFormattable sf)
            {
                while (true)
                {
                    if (sf.TryFormat(_buffer.AsSpan(_pos), out int written, ReadOnlySpan<char>.Empty, CultureInfo.InvariantCulture))
                    {
                        _pos += written;
                        return;
                    }
                    Grow();
                }
            }
            string s = value?.ToString();
            if (!string.IsNullOrEmpty(s)) AppendLiteral(s);
        }

        public void AppendFormatted<T>(T value, string format)
        {
            if (!_enabled) return;
            if (value is ISpanFormattable sf)
            {
                while (true)
                {
                    if (sf.TryFormat(_buffer.AsSpan(_pos), out int written, format.AsSpan(), CultureInfo.InvariantCulture))
                    {
                        _pos += written;
                        return;
                    }
                    Grow();
                }
            }
            if (value is IFormattable f)
            {
                AppendLiteral(f.ToString(format, CultureInfo.InvariantCulture));
                return;
            }
            string s = value?.ToString();
            if (!string.IsNullOrEmpty(s)) AppendLiteral(s);
        }

        public void AppendFormatted<T>(T value, int alignment)
        {
            // Alignment is rare in our logs — fall through ToString with padding.
            if (!_enabled) return;
            string s = value?.ToString() ?? "";
            AppendPadded(s, alignment);
        }

        public void AppendFormatted<T>(T value, int alignment, string format)
        {
            if (!_enabled) return;
            string s = value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
            AppendPadded(s, alignment);
        }

        public void AppendFormatted(ReadOnlySpan<char> value)
        {
            if (!_enabled) return;
            EnsureRemaining(value.Length);
            value.CopyTo(_buffer.AsSpan(_pos));
            _pos += value.Length;
        }

        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string format = null)
        {
            if (!_enabled) return;
            if (alignment == 0)
            {
                EnsureRemaining(value.Length);
                value.CopyTo(_buffer.AsSpan(_pos));
                _pos += value.Length;
                return;
            }
            AppendPaddedSpan(value, alignment);
        }

        public void AppendFormatted(string value)
        {
            if (!_enabled || value == null) return;
            AppendLiteral(value);
        }

        public void AppendFormatted(string value, int alignment = 0, string format = null)
        {
            if (!_enabled || value == null) return;
            if (alignment == 0) { AppendLiteral(value); return; }
            AppendPadded(value, alignment);
        }

        /// <summary>Transfer ownership of the rented buffer to the caller.
        /// After this call the handler is empty; the caller is responsible
        /// for returning the buffer to <see cref="ArrayPool{T}.Shared"/>.</summary>
        internal (char[] Buffer, int Length) Take()
        {
            var b = _buffer; var l = _pos;
            _buffer = null; _pos = 0;
            return (b, l);
        }

        /// <summary>Fallback for callers that genuinely need a string — eg.
        /// when the message is going to a non-pooled sink. Not used on the
        /// hot path (Debug() calls Take()).</summary>
        public string ToStringAndClear()
        {
            if (!_enabled) return "";
            string s = _buffer == null ? "" : new string(_buffer, 0, _pos);
            if (_buffer != null) ArrayPool<char>.Shared.Return(_buffer);
            _buffer = null;
            _pos = 0;
            return s;
        }

        private void EnsureRemaining(int needed)
        {
            if (_buffer == null) return;
            if (_pos + needed > _buffer.Length) Grow(_pos + needed);
        }

        private void Grow(int minRequired = 0)
        {
            int newSize = Math.Max(_buffer.Length * 2, minRequired);
            var newBuf = ArrayPool<char>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _pos).CopyTo(newBuf);
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = newBuf;
        }

        private void AppendPadded(string value, int alignment)
        {
            int padLeft = alignment > 0 ? Math.Max(0, alignment - value.Length) : 0;
            int padRight = alignment < 0 ? Math.Max(0, -alignment - value.Length) : 0;
            EnsureRemaining(value.Length + padLeft + padRight);
            for (int i = 0; i < padLeft; i++) _buffer[_pos++] = ' ';
            value.AsSpan().CopyTo(_buffer.AsSpan(_pos));
            _pos += value.Length;
            for (int i = 0; i < padRight; i++) _buffer[_pos++] = ' ';
        }

        private void AppendPaddedSpan(ReadOnlySpan<char> value, int alignment)
        {
            int padLeft = alignment > 0 ? Math.Max(0, alignment - value.Length) : 0;
            int padRight = alignment < 0 ? Math.Max(0, -alignment - value.Length) : 0;
            EnsureRemaining(value.Length + padLeft + padRight);
            for (int i = 0; i < padLeft; i++) _buffer[_pos++] = ' ';
            value.CopyTo(_buffer.AsSpan(_pos));
            _pos += value.Length;
            for (int i = 0; i < padRight; i++) _buffer[_pos++] = ' ';
        }
    }
}
