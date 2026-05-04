using Godot;

namespace MonkeNet.Shared;

[GlobalClass]
public partial class MonkeLogger : Node
{
    public static MonkeLogger Instance { get; private set; }

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    // Set by the client when it receives a session token; cleared on disconnect.
    // Server leaves this null — server-side logs embed the token per-message instead.
    public static string CurrentToken { get; set; }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);

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
        if (level == "ERROR")
            GD.PrintErr(line);
        else
            GD.Print(line);
    }
}
