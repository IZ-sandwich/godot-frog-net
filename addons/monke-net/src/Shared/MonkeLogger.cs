using System;
using System.IO;
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

    // Direct file sink. We don't rely on Godot's stdout-based file logger because the
    // editor's output panel drops chunks ("[output overflow, print less text!]") when a
    // single frame prints too much, and depending on the build the dropped lines may
    // not survive in user://logs/godot.log either. Writing here ourselves with AutoFlush
    // guarantees every line lands on disk regardless of console pressure.
    private static StreamWriter _file;
    private static readonly object _fileLock = new();
    private static string _filePath;
    /// <summary>Absolute path of the log file this process is writing to, or null
    /// if file logging is disabled / failed to open. Surfaced so a test harness
    /// can copy the live log out at run end without having to rediscover the path.</summary>
    public static string FilePath => _filePath;

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
            _file = new StreamWriter(new FileStream(_filePath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            GD.Print($"[MonkeLogger] writing full log to {_filePath}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MonkeLogger] failed to open log file: {ex.Message}");
            _file = null;
        }
    }

    private static void CloseLogFile()
    {
        lock (_fileLock)
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
    }

    // Set by the client when it receives a session token; cleared on disconnect.
    // Server leaves this null — server-side logs embed the token per-message instead.
    public static string CurrentToken { get; set; }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);

    /// <summary>
    /// Emits a debug-level log line, but only when <see cref="DebugEnabled"/> is true on
    /// the autoload instance. Safe to call from anywhere — no-op when the autoload isn't
    /// in the tree (e.g. headless test setups that don't load MainScene).
    /// </summary>
    public static void Debug(string message)
    {
        if (Instance == null || !Instance.DebugEnabled) return;
        Log("DEBUG", message);
    }

    private static int _markCounter = 0;

    /// <summary>
    /// Emits a distinctive [MARK] line so a user can drop a needle into the log and
    /// later jump back to that point when reconstructing a session. Always prints
    /// regardless of <see cref="DebugEnabled"/> — the whole point is being findable.
    /// Each call gets an incrementing id so multiple marks in one session stay
    /// distinguishable.
    /// </summary>
    public static void Mark(int tick, string label = null)
    {
        int id = System.Threading.Interlocked.Increment(ref _markCounter);
        string suffix = string.IsNullOrEmpty(label) ? "" : $" label='{label}'";
        Log("MARK", $"#{id} tick={tick}{suffix}");
    }

    private static void Log(string level, string message)
    {
        string dt = Time.GetDatetimeStringFromSystem(false, false);
        string ms = (Time.GetTicksMsec() % 1000).ToString("D3");
        int peerId = 0;
        try
        {
            var peer = Instance?.Multiplayer?.MultiplayerPeer;
            if (peer != null && peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected)
                peerId = Instance.Multiplayer.GetUniqueId();
        }
        catch { }

        string tok = CurrentToken?.Length >= 4 ? CurrentToken[^4..] : "----";
        string line = $"[{dt}.{ms}] [{peerId}]\t\t[tok:{tok}] [{level}]\t{message}";

        // File sink first — this is the source of truth and must never be skipped, even
        // when the editor's output panel is overflowing.
        if (_file != null)
        {
            lock (_fileLock)
            {
                try { _file?.WriteLine(line); } catch { }
            }
        }

        // Editor/console sink. DEBUG is intentionally suppressed here: it's the highest-
        // volume level and the main cause of "[output overflow, print less text!]" in
        // the editor. Debug remains in the file log above.
        if (level == "DEBUG") return;
        if (level == "ERROR")
            GD.PrintErr(line);
        else
            GD.Print(line);
    }
}
