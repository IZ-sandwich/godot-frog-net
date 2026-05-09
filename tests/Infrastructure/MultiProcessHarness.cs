using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// Test-side orchestration of multi-process integration tests. Spawns child
/// Godot processes, each loading
/// <c>res://tests/MultiProcess/harness.tscn</c> with role and port arguments,
/// then drives them via line-delimited JSON over TCP.
///
/// Each child process has its OWN Godot World3D, physics space, MonkeNet
/// singletons, and operating system process — eliminating the same-process-
/// shared-state concerns of in-process multi-client tests.
///
/// Typical usage:
/// <code>
///   var orch = new MultiProcessOrchestrator(godotBinPath: GodotBin, projectPath: ProjectPath);
///   var server = orch.Spawn("server", enetPort: 9100, label: "srv");
///   var client = orch.Spawn("client", enetPort: 9100, label: "c1");
///   server.WaitReady();
///   client.WaitReady(networkReady: true);  // waits for ENet handshake
///   server.Send(new { cmd = "spawn-ball", authority = 0, position = new[]{0,5,0} });
///   ...
///   orch.Dispose(); // kills all children
/// </code>
/// </summary>
public class MultiProcessOrchestrator : IDisposable
{
    private readonly string _godotBin;
    private readonly string _projectPath;
    private readonly List<TestProcess> _processes = new();
    private static int _nextOrchPort = 9500;

    public MultiProcessOrchestrator(string godotBinPath, string projectPath)
    {
        _godotBin = godotBinPath;
        _projectPath = projectPath;
    }

    public TestProcess Spawn(string role, int enetPort, string label = null,
        string serverAddr = "127.0.0.1")
    {
        int orchPort = Interlocked.Increment(ref _nextOrchPort);
        label ??= role;

        // Normalize the project path to forward slashes — Godot's res:// resolver
        // is happier with POSIX-style separators on Windows even though Win32 APIs
        // accept both. Backslashes from DirectoryInfo.FullName have caused symptom-
        // identical "Cannot open file 'res://...'" errors despite the file existing.
        string projectPath = _projectPath.Replace('\\', '/');

        // Launch MainScene with the --test-harness user arg so MainScene's
        // _Ready detects test-harness mode, hides its UI, and instantiates a
        // MultiClientHarness child node. This works around a Godot resource-
        // loading quirk where a stand-alone harness scene fails to load when
        // launched from inside the gdUnit4 test runner — MainScene is the
        // project's main scene and is therefore reliably loadable.
        var args = new List<string>
        {
            "--headless",
            "--path", projectPath,
            "res://demo/MainScene.tscn",
            "--",
            "--test-harness",
            $"--role={role}",
            $"--enet-port={enetPort}",
            $"--orch-port={orchPort}",
            $"--label={label}",
        };
        if (role == "client") args.Add($"--server-addr={serverAddr}");

        var psi = new ProcessStartInfo(_godotBin)
        {
            // Explicitly set the child's CWD to the project root. Without this,
            // the child inherits the dotnet test runner's CWD (typically
            // <project>/tests/) which causes Godot to mis-resolve res:// paths
            // even with --path set.
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to spawn Godot for role={role}");

        // Capture stdout/stderr to in-memory buffers in the background so we can
        // include them in error messages if the child fails to come up.
        var stdoutBuf = new System.Text.StringBuilder();
        var stderrBuf = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdoutBuf) stdoutBuf.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderrBuf) stderrBuf.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var tp = new TestProcess(proc, label, orchPort, stdoutBuf, stderrBuf);
        _processes.Add(tp);
        tp.ConnectOrchSocket(timeoutMs: 30_000);
        return tp;
    }

    public void Dispose()
    {
        foreach (var tp in _processes)
        {
            try { tp.Dispose(); } catch { /* best effort */ }
        }
        _processes.Clear();
    }
}

/// <summary>
/// One child Godot process running the harness. Wraps the orch TCP socket and
/// provides typed Send/Wait helpers.
/// </summary>
public class TestProcess : IDisposable
{
    public string Label { get; }
    public int OrchPort { get; }

    private readonly Process _process;
    private readonly System.Text.StringBuilder _stdoutBuf;
    private readonly System.Text.StringBuilder _stderrBuf;
    private TcpClient _orchClient;
    private NetworkStream _orchStream;
    private StreamReader _reader;
    private StreamWriter _writer;
    private bool _disposed;

    internal TestProcess(Process process, string label, int orchPort,
        System.Text.StringBuilder stdoutBuf, System.Text.StringBuilder stderrBuf)
    {
        _process = process;
        Label = label;
        OrchPort = orchPort;
        _stdoutBuf = stdoutBuf;
        _stderrBuf = stderrBuf;
    }

    /// <summary>Polls the orch port until accept succeeds (child takes ~1-3 s
    /// to bring up the listener) or <paramref name="timeoutMs"/> elapses.</summary>
    internal void ConnectOrchSocket(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Exception lastErr = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                string stderrSnap, stdoutSnap;
                lock (_stderrBuf) stderrSnap = _stderrBuf.ToString();
                lock (_stdoutBuf) stdoutSnap = _stdoutBuf.ToString();
                throw new InvalidOperationException(
                    $"[{Label}] child exited (code {_process.ExitCode}) before orch socket opened.\n--- child stdout ---\n{stdoutSnap}\n--- child stderr ---\n{stderrSnap}");
            }
            try
            {
                var c = new TcpClient();
                c.Connect("127.0.0.1", OrchPort);
                _orchClient = c;
                _orchStream = c.GetStream();
                _reader = new StreamReader(_orchStream, new UTF8Encoding(false));
                _writer = new StreamWriter(_orchStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                return;
            }
            catch (Exception e) { lastErr = e; Thread.Sleep(150); }
        }
        // Include child stdout/stderr in the timeout message so test logs show
        // what Godot reported when bringing up the harness scene.
        string stdout, stderr;
        lock (_stdoutBuf) stdout = _stdoutBuf.ToString();
        lock (_stderrBuf) stderr = _stderrBuf.ToString();
        const int Tail = 4000;
        if (stdout.Length > Tail) stdout = stdout.Substring(stdout.Length - Tail);
        if (stderr.Length > Tail) stderr = stderr.Substring(stderr.Length - Tail);
        throw new TimeoutException(
            $"[{Label}] orch socket on port {OrchPort} never accepted within {timeoutMs} ms. last err: {lastErr?.Message}\n--- child stdout (tail) ---\n{stdout}\n--- child stderr (tail) ---\n{stderr}");
    }

    /// <summary>Sends a command and reads the single-line JSON response.</summary>
    public JsonDocument Send(object request, int timeoutMs = 10_000)
    {
        if (_writer == null) throw new InvalidOperationException($"[{Label}] not connected");
        string line = JsonSerializer.Serialize(request);
        _writer.WriteLine(line);

        _orchStream.ReadTimeout = timeoutMs;
        string resp = _reader.ReadLine();
        if (resp == null) throw new InvalidOperationException($"[{Label}] orch socket closed before response");
        var doc = JsonDocument.Parse(resp);
        if (doc.RootElement.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
        {
            string err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "<no error>";
            throw new InvalidOperationException($"[{Label}] command failed: {err}");
        }
        return doc;
    }

    /// <summary>Polls the harness's "ready" command until <paramref name="networkReady"/>
    /// matches (or until <paramref name="timeoutMs"/> elapses). For clients, networkReady=true
    /// means the ENet handshake completed and the client clock is synced.</summary>
    public void WaitReady(bool networkReady = false, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var doc = Send(new { cmd = "ready" }, timeoutMs: 5_000);
            var data = doc.RootElement.GetProperty("data");
            bool isReady = data.GetProperty("ready").GetBoolean();
            bool isNetReady = data.GetProperty("networkReady").GetBoolean();
            if (isReady && (!networkReady || isNetReady)) return;
            Thread.Sleep(150);
        }
        throw new TimeoutException($"[{Label}] not ready within {timeoutMs} ms (networkReady={networkReady})");
    }

    /// <summary>Coarse fallback — sleeps wall-clock time approximately equal to N
    /// physics ticks at 60 Hz. Use <see cref="WaitForTicks"/> instead when the
    /// child has its own physics loop and you need to advance N ticks ON THAT
    /// process, not just N ticks of wall time.</summary>
    public static void SleepTicks(int ticks) => Thread.Sleep(ticks * 1000 / 60 + 50);

    /// <summary>Polls the harness's tick-count command until at least
    /// <paramref name="ticks"/> have elapsed since the call started. Cleaner
    /// than wall-clock sleep — works correctly even if the child Godot process
    /// is slow to start, busy with other work, or running on a loaded machine.</summary>
    public void WaitForTicks(int ticks, int timeoutMs = 30_000)
    {
        long startTicks = ReadTickCount();
        long target = startTicks + ticks;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadTickCount() >= target) return;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"[{Label}] did not advance {ticks} physics ticks within {timeoutMs} ms (started at {startTicks}, last seen {ReadTickCount()})");
    }

    private long ReadTickCount()
    {
        using var doc = Send(new { cmd = "tick-count" });
        return doc.RootElement.GetProperty("data").GetProperty("ticks").GetInt64();
    }

    /// <summary>The peer's Godot multiplayer network ID. For the server this is 1;
    /// for clients it's the dynamically assigned ID (typically 2, 3, ... in
    /// connection order, but never assume order — use this method).</summary>
    public int NetworkId
    {
        get
        {
            using var doc = Send(new { cmd = "get-network-id" });
            return doc.RootElement.GetProperty("data").GetProperty("networkId").GetInt32();
        }
    }

    private string ReadStderrSafe()
    {
        try { return _process.StandardError.ReadToEnd(); }
        catch { return "<unavailable>"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_writer != null) Send(new { cmd = "shutdown" }, timeoutMs: 2_000);
        }
        catch { /* best effort */ }

        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _orchStream?.Dispose(); } catch { }
        try { _orchClient?.Dispose(); } catch { }

        try
        {
            if (!_process.WaitForExit(3_000)) _process.Kill(entireProcessTree: true);
        }
        catch { try { _process.Kill(entireProcessTree: true); } catch { } }
    }
}
