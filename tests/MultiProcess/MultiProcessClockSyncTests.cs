using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using GdUnit4;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-CLOCK-01..02: clock-sync convergence in the multi-process harness.
///
/// Spawns a real server + real client and samples each side's
/// <c>clock-state</c> at ~20 Hz from the moment the client process is up.
/// Tracks the clock gap (<c>clientSyncedTick − serverTick − latency</c>) over
/// time and asserts that it converges into a Photon-Fusion-2-class window
/// (within a few ticks, within a second of connecting) — i.e. the client's
/// prediction tick lands close to the server's authoritative tick after the
/// expected network latency.
///
/// Two artefact SVGs are written for visual inspection:
///   - <c>clock_sync.by_tick.svg</c>       X axis = client synced tick. Lines up
///                                         with CLIENT-TICK in the MonkeLogger output.
///   - <c>clock_sync.by_wallclock.svg</c>  X axis = wall-clock ms since first sample.
///                                         Answers "how fast in real time does sync converge?".
///
/// Plus <c>clock_sync.csv</c> with the raw trace, and the per-process
/// MonkeLogger files copied alongside.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessClockSyncTests
{
    private const string ArtifactRoot = "TestResults/ClockSync";

    // Sample cadence + total run length. 50 ms × 200 = 10 s of trace, plenty
    // to see the ramp-up and a few steady-state cycles after the first sync
    // window completes.
    private const int SampleIntervalMs = 50;
    private const int RunMs = 10_000;

    // Photon-Fusion-2-class convergence target.
    //
    // Measured from the first sample (= moment the client process is observable
    // by the orchestrator, roughly process-start) to the first run of converged
    // samples where |client_synced − server − latency| ≤ ConvergedAbsGapTicks.
    //
    // Fusion documents "0.5-1 s on LAN to a stable few-tick prediction"; we set
    // the bar at 1000 ms / ±5 ticks / 4-sample (200 ms) stable streak.
    //
    // This deliberately FAILS the current MonkeNet implementation, where the
    // first averaged correction only fires after _sampleSize (3) samples at
    // 1 Hz = ~3 s, leaving the client at gap≈-60 ticks for the first 2-3 s.
    // The follow-up clock-sync improvements (per-sample correction + fast-start
    // ramp + smooth ramp instead of a big step) bring this under 1 s.
    private const int ConvergeWithinMs = 1_000;
    private const int ConvergedAbsGapTicks = 5;
    private const int ConsecutiveConvergedSamples = 4;   // 4 × 50 ms = 200 ms stable

    private static int _enetPortCounter = 9400;

    private string _godotBin;
    private string _projectPath;
    private MultiProcessOrchestrator _orch;

    [BeforeTest]
    public void SetUp()
    {
        _godotBin = System.Environment.GetEnvironmentVariable("GODOT_BIN");
        if (string.IsNullOrEmpty(_godotBin) || !File.Exists(_godotBin)) return;

        _projectPath = ResolveProjectPath();
        Directory.CreateDirectory(Path.Combine(_projectPath, ArtifactRoot));
        _orch = new MultiProcessOrchestrator(_godotBin, _projectPath);
    }

    [AfterTest]
    public void TearDown()
    {
        _orch?.Dispose();
        _orch = null;
    }

    // MP-CLOCK-01 ──────────────────────────────────────────────────────────────
    // Convergence target. Connects a client, samples both sides' clock state
    // for 10 s, and asserts the gap is within budget within ConvergeWithinMs.
    [TestCase]
    public void MultiProcess_ClockSync_ConvergesWithinFusionClassWindow()
    {
        if (_orch == null) return;

        int port = NextPort();
        var server = _orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        // Spawn the client and DO NOT wait for it to be networkReady — we want to
        // capture the entire ramp-up including the pre-sync window. Sampling
        // starts as soon as the orch socket accepts, then we identify the
        // "connect moment" post-hoc as the first sample where networkReady=true.
        var client = _orch.Spawn("client", enetPort: port, label: "c1");

        var samples = new List<ClockSyncPlot.Sample>();
        long t0Ms = -1;
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < RunMs)
        {
            var sample = SampleClocks(server, client);
            if (sample == null) { Thread.Sleep(SampleIntervalMs); continue; }
            if (t0Ms < 0) t0Ms = sample.ClientWallMs;
            samples.Add(sample);
            Thread.Sleep(SampleIntervalMs);
        }

        // Populate client.RemoteLogPath via one ready cmd so WriteArtifacts can
        // copy the per-process log. We intentionally don't WaitReady at the
        // start so that pre-network-ready samples are captured in the trace.
        try { client.WaitReady(networkReady: false, timeoutMs: 2_000); } catch { }

        WriteArtifacts("clock_sync", samples, t0Ms, server, client);

        // For diagnostic context: when did networkReady first transition true?
        long connectMsRel = -1;
        foreach (var s in samples)
        {
            if (s.NetworkReady) { connectMsRel = s.ClientWallMs - t0Ms; break; }
        }

        long convergedAtMsRel = FindConvergenceMsRel(samples, t0Ms);

        AssertThat(convergedAtMsRel)
            .OverrideFailureMessage(
                $"clock-sync gap never converged below ±{ConvergedAbsGapTicks} ticks for " +
                $"{ConsecutiveConvergedSamples} consecutive samples ({ConsecutiveConvergedSamples * SampleIntervalMs} ms). " +
                $"Trace + plot at {ArtifactRoot}/clock_sync.{{csv,by_tick.svg,by_wallclock.svg}}. " +
                $"Last sample's gap was {(samples.Count > 0 ? samples[^1].ClientSyncedTick - samples[^1].ServerTick - samples[^1].LatencyTicks : 0)} ticks.")
            .IsGreaterEqual(0);

        Godot.GD.Print($"[MP-CLOCK] networkReady at +{connectMsRel} ms, gap converged at +{convergedAtMsRel} ms");

        AssertThat(convergedAtMsRel)
            .OverrideFailureMessage(
                $"clock-sync took {convergedAtMsRel} ms from process start to converge (Fusion-class budget {ConvergeWithinMs} ms). " +
                $"networkReady fired at +{connectMsRel} ms. Plot at {ArtifactRoot}/clock_sync.by_wallclock.svg")
            .IsLessEqual(ConvergeWithinMs);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int NextPort() => Interlocked.Increment(ref _enetPortCounter);

    // One paired sample. Calls server then client (clock-state is idempotent +
    // cheap). Returns null on any error so the sampling loop can press on.
    private static ClockSyncPlot.Sample SampleClocks(TestProcess server, TestProcess client)
    {
        try
        {
            using var sDoc = server.Send(new { cmd = "clock-state" });
            using var cDoc = client.Send(new { cmd = "clock-state" });
            var s = sDoc.RootElement.GetProperty("data");
            var c = cDoc.RootElement.GetProperty("data");
            return new ClockSyncPlot.Sample
            {
                ServerWallMs = s.GetProperty("wallMs").GetInt64(),
                ClientWallMs = c.GetProperty("wallMs").GetInt64(),
                ServerTick = s.GetProperty("serverTick").GetInt32(),
                ClientRawTick = c.GetProperty("rawTick").GetInt32(),
                ClientSyncedTick = c.GetProperty("syncedTick").GetInt32(),
                LatencyTicks = c.GetProperty("averageLatencyTicks").GetInt32(),
                JitterTicks = c.GetProperty("jitterTicks").GetInt32(),
                OffsetTicks = c.GetProperty("averageOffsetTicks").GetInt32(),
                SyncWindowsApplied = c.GetProperty("syncWindowsApplied").GetInt32(),
                NetworkReady = c.GetProperty("networkReady").GetBoolean(),
            };
        }
        catch
        {
            return null;
        }
    }

    // Walks the trace looking for the first window of ConsecutiveConvergedSamples
    // samples where |gap| ≤ ConvergedAbsGapTicks. Returns the ms-since-t0 at the
    // start of the streak, or -1 if no such streak exists.
    private static long FindConvergenceMsRel(List<ClockSyncPlot.Sample> samples, long t0Ms)
    {
        int streak = 0;
        int streakStartIdx = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            int gap = s.ClientSyncedTick - s.ServerTick - s.LatencyTicks;
            if (Math.Abs(gap) <= ConvergedAbsGapTicks)
            {
                if (streak == 0) streakStartIdx = i;
                streak++;
                if (streak >= ConsecutiveConvergedSamples)
                    return samples[streakStartIdx].ClientWallMs - t0Ms;
            }
            else
            {
                streak = 0;
                streakStartIdx = -1;
            }
        }
        return -1;
    }

    private void WriteArtifacts(string label, List<ClockSyncPlot.Sample> samples, long t0Ms,
        TestProcess server, TestProcess client)
    {
        var dir = Path.Combine(_projectPath, ArtifactRoot);
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, label + ".csv");
        var svgByTick = Path.Combine(dir, label + ".by_tick.svg");
        var svgByWall = Path.Combine(dir, label + ".by_wallclock.svg");
        ClockSyncPlot.WriteCsv(csv, samples, t0Ms);
        ClockSyncPlot.WriteSvgByNetworkTick(svgByTick, samples, "Clock sync — by client synced tick");
        ClockSyncPlot.WriteSvgByWallClock(svgByWall, samples, "Clock sync — by wall-clock ms", t0Ms);
        Godot.GD.Print($"[MP-CLOCK] wrote {csv}, {svgByTick}, {svgByWall} ({samples.Count} samples)");

        CopyProcessLog(dir, server.RemoteLogPath, label + ".server.log");
        CopyProcessLog(dir, client.RemoteLogPath, label + ".client.log");
    }

    private static void CopyProcessLog(string artifactDir, string srcPath, string targetName)
    {
        if (string.IsNullOrEmpty(srcPath)) return;
        if (!File.Exists(srcPath)) return;
        try
        {
            using var src = new FileStream(srcPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(Path.Combine(artifactDir, targetName), FileMode.Create, System.IO.FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[MP-CLOCK] failed to copy {srcPath}: {ex.Message}");
        }
    }

    private static string ResolveProjectPath()
    {
        var dir = new DirectoryInfo(System.Environment.CurrentDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate project.godot from current working directory");
    }
}
