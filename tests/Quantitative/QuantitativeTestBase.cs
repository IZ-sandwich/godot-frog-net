using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative;

/// <summary>
/// Runs the quantitative test matrix: each scenario × each condition produces
/// one row in the summary CSV. Per cell, the runner spawns a fresh server +
/// client pair routed through a <see cref="UdpRelay"/> configured for that
/// cell's conditions, then delegates to <see cref="IScenario.Setup"/> and
/// <see cref="IScenario.Run"/>.
///
/// <para>
/// Why one fresh process pair per cell: clock-sync convergence is one of the
/// metrics, so it must be observed from a cold start. Re-using a client across
/// conditions would let the previous cell's synced clock and prediction
/// history bleed into the next cell.
/// </para>
/// </summary>
public abstract class QuantitativeTestBase : MultiProcessTestBase
{
    /// <summary>Subdirectory under <c>TestResults/</c>. Default is "Quantitative";
    /// override per-test if you want a separate folder per matrix.</summary>
    protected override string ArtifactSubdir => "Quantitative";

    /// <summary>Run every (scenario × condition) cell and write the summary CSV
    /// + per-scenario radar plots. The summary CSV filename embeds the run
    /// timestamp + git commit; see <see cref="MetricsSummaryCsv"/>.</summary>
    protected void RunMatrix(IScenario[] scenarios)
    {
        if (Orch == null)
        {
            GD.Print("[QuantitativeTestBase] GODOT_BIN not set — skipping matrix");
            return;
        }
        _lastRunScenarios = scenarios;
        // Discard the test-base orchestrator; we own per-cell lifecycle.
        Orch?.Dispose();

        // Compute the per-run folder once so cells (which write MP4 + debug
        // logs) and the post-run writers (CSV + dashboard + strip plots)
        // all target the same directory.
        string runFolderName = MetricsSummaryCsv.RunFolderName(ProjectPath);
        _currentRunArtifactDir = System.IO.Path.Combine(
            ProjectPath, "TestResults", ArtifactSubdir, runFolderName);
        System.IO.Directory.CreateDirectory(_currentRunArtifactDir);
        GD.Print($"[QuantitativeTestBase] run folder → {_currentRunArtifactDir}");

        var summary = new MetricsSummaryCsv();
        var perScenarioRows = new Dictionary<string, List<SyncMetrics.Summary>>();
        // Stash raw |Δv|² distributions per (scenario, condition) so the per-
        // scenario CDF writer can build a one-curve-per-condition plot without
        // re-running the cells. Keyed by scenario id; inner dict keyed by
        // condition id. Cells that don't exercise M14 just don't add an entry.
        var perScenarioM14Samples = new Dictionary<string, Dictionary<string, IReadOnlyList<float>>>();

        foreach (var scenario in scenarios)
        {
            perScenarioRows[scenario.Id] = new List<SyncMetrics.Summary>();
            perScenarioM14Samples[scenario.Id] = new Dictionary<string, IReadOnlyList<float>>();
            foreach (var condition in scenario.Conditions)
            {
                GD.Print($"[QuantitativeTestBase] running {scenario.Id} × {condition.Id} ({condition.Label})");
                SyncMetrics.Summary row;
                IReadOnlyList<float> m14Samples = null;
                try
                {
                    row = RunOneCell(scenario, condition, out m14Samples);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[QuantitativeTestBase] {scenario.Id} × {condition.Id} FAILED: {ex.Message}");
                    // Surface the failure as a row with NaN-flagged metrics
                    // (M1=-1) rather than silently dropping it — operators
                    // reading the CSV need to see which cells didn't complete.
                    row = new SyncMetrics.Summary
                    {
                        Scenario = scenario.Id,
                        Condition = condition.Id,
                        M1_ClockConvergenceTicks = -1,
                    };
                }
                summary.Add(row);
                perScenarioRows[scenario.Id].Add(row);
                if (m14Samples != null && m14Samples.Count > 0)
                    perScenarioM14Samples[scenario.Id][condition.Id] = m14Samples;
            }
        }

        string artifactDir = _currentRunArtifactDir;
        string summaryPath = summary.Save(artifactDir, ProjectPath);
        GD.Print($"[QuantitativeTestBase] summary written → {summaryPath}");

        // Heatmap dashboard: rows = (scenario × condition), columns = metrics.
        // One-page health-of-the-matrix view; readers drill into individual
        // scenarios via the per-scenario strip plots written below.
        WriteDashboard(System.IO.Path.Combine(artifactDir, "dashboard.svg"), perScenarioRows);

        // Per-scenario strip plots. Each scenario gets ONE strip plot showing
        // only its applicable metrics — irrelevant rows (e.g. M3b ext-force
        // on the idle baseline) are filtered out instead of rendering as a
        // strip-wide row of N/A markers. The scenario's full condition matrix
        // is laid out as markers per strip.
        var scenariosById = new Dictionary<string, IScenario>();
        foreach (var s in _lastRunScenarios) scenariosById[s.Id] = s;
        foreach (var (scenarioId, rows) in perScenarioRows)
        {
            if (rows.Count < 1) continue;
            if (!scenariosById.TryGetValue(scenarioId, out var scenario)) continue;
            WriteScenarioStripPlot(
                System.IO.Path.Combine(artifactDir, scenarioId + ".strip.svg"),
                scenarioId, scenario.ApplicableMetrics, rows);

            // Per-scenario M14 distribution plot: log-x CDF of |Δv| per
            // condition. The strip plot reports RMS + p50 as scalars; the
            // CDF lets a reader see whether degradation under bad networks
            // is "tail spikes" (a few large Δv events that drag RMS up but
            // leave p50 untouched) vs "uniformly noisier" (the whole curve
            // shifts right). Only written when at least one condition
            // produced samples.
            if (perScenarioM14Samples.TryGetValue(scenarioId, out var perCondSamples)
                && perCondSamples.Count > 0)
            {
                WriteVisualSmoothnessCdf(
                    System.IO.Path.Combine(artifactDir, scenarioId + ".m14-distribution.svg"),
                    scenarioId, perCondSamples);
            }
        }
    }

    // Stash so per-scenario writers can look up applicability masks.
    private IScenario[] _lastRunScenarios = Array.Empty<IScenario>();
    // Per-run artifact directory shared by RunMatrix (radar/dashboard SVG
    // outputs) and RunOneCell (per-cell MP4 + debug-log writers). Set once
    // per RunMatrix invocation so every artifact lands in the same per-run
    // folder.
    private string _currentRunArtifactDir = null;

    private SyncMetrics.Summary RunOneCell(IScenario scenario, NetworkCondition condition,
        out IReadOnlyList<float> m14DvSquaredSamples)
    {
        m14DvSquaredSamples = null;
        int serverPort = NextPort();
        using var relay = new UdpRelay(serverPort);
        relay.SetConditions(condition.LatencyMs, condition.JitterMs, condition.LossRate);

        using var orch = new MultiProcessOrchestrator(GodotBin, ProjectPath);
        var server = orch.Spawn("server", enetPort: serverPort, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        // Client connects to the relay's listen port, not the server's real
        // port — that's how the injected conditions take effect.
        string videoPath = null;
        if (scenario.RecordVideo && _currentRunArtifactDir != null)
        {
            videoPath = System.IO.Path.Combine(_currentRunArtifactDir,
                $"{scenario.Id}.{condition.Id}.mp4");
        }
        // Defer recorder construction so the captured MP4 doesn't include the
        // ~5 s of clock-sync sampling that runs before scenario.Setup. The
        // recorder is started below right before Setup, so the video begins
        // just as the scenario spawns its entities — which is what a reader
        // actually wants to see.
        var client = orch.Spawn("client", enetPort: relay.ListenPort, label: "c1",
            recordVideoPath: videoPath, deferVideoStart: videoPath != null);
        client.WaitReady(networkReady: true, timeoutMs: 60_000);

        // Spawn a second client when the scenario opts in (e.g. S5 multi-
        // client shared physics). Both clients go through the same relay so
        // both see the same injected network conditions. Once spawned, the
        // scenario gets a chance to stash the reference via SetObserver.
        TestProcess observer = null;
        if (scenario.RequiresObserver)
        {
            observer = orch.Spawn("client", enetPort: relay.ListenPort, label: "c2");
            observer.WaitReady(networkReady: true, timeoutMs: 60_000);
            scenario.SetObserver(observer);
        }

        // T1 per-prop tier icons. Bypasses SpawnTriad's default-on path
        // because RunOneCell hand-rolls the spawn lifecycle for per-cell
        // recorder + relay handling. Recorded MP4s for prop-heavy scenarios
        // (S4, S7) show R/I glyphs so a reader can correlate misprediction
        // events with contact-upgrade timing without grepping logs.
        EnableTierIcons(client);
        if (observer != null) EnableTierIcons(observer);

        var metrics = new SyncMetrics();

        // Sample clock-sync convergence aggressively for ~5 seconds — but only
        // when the scenario actually exposes M1/M2 (just S2). M1/M2 are a pure
        // function of the NetworkCondition and the library; the scene contents
        // don't affect them. S2 runs the full condition matrix, so measuring
        // convergence there covers every condition once for the whole suite —
        // every other scenario can skip the 5 s sampling window and just wait
        // briefly for the clock to converge before its action begins.
        if ((scenario.ApplicableMetrics & MetricKey.ClockConvergence) != 0)
        {
            // The library's steady-state clock-sync algorithm uses 11 samples
            // at 1s intervals before applying an averaged correction, so
            // anything shorter than ~3s would only ever see the fast-start
            // phase — but the metric is supposed to characterise the
            // cold-start-to-steady-state behaviour.
            SampleClockConvergence(server, client, metrics, samples: 150, intervalMs: 35);
        }
        else
        {
            // Still wait for the clock to converge before the scenario starts
            // spawning entities, so the trace measures physics misprediction
            // rather than "client clock catching up to server". Per-condition
            // timeout sized to ~p99 × 1.5 of expected cold-start convergence
            // (NetworkCondition.ClockSyncTimeoutMs) — typically returns much
            // sooner; the budget only matters on tail-latency runs.
            WaitForClockSync(server, client, maxGapTicks: 5,
                timeoutMs: condition.ClockSyncTimeoutMs);
        }

        // Arm 1 Hz bandwidth bucketing for the observation window. The harness
        // drains pre-window bytes (warm-up handshake / clock-sync) inside this
        // call so the first bucket starts clean. Sampling stays armed across
        // scenario.Setup + scenario.Run; the spawn burst is deliberately
        // included as part of the per-second distribution rather than excluded
        // (it's part of the real network behaviour and is what makes a single
        // averaged-over-the-window sample so noisy — bucketing absorbs it as
        // one tall bar in the distribution instead of dragging the mean).
        try { client.Send(new { cmd = "bandwidth-reset" }); } catch { }

        // Kick off the deferred recorder (no-op if this cell isn't recording or
        // the recorder is already running). Placed here so the MP4 starts just
        // before entities spawn, trimming the cold-start clock-sync window.
        if (!string.IsNullOrEmpty(videoPath))
        {
            try { using var _ = client.Send(new { cmd = "start-recording" }); }
            catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] start-recording failed: {ex.Message}"); }
        }

        // Optional profiler-attach pause. When MONKENET_TEST_PROFILE=1, hold
        // before scenario.Setup so the user can hook dotnet-trace onto the
        // client + server PIDs before the rollback storm starts. The duration
        // defaults to 20 s; override with MONKENET_TEST_PROFILE_PAUSE_MS. The
        // pause resolves early if a trigger file appears at
        // <projectPath>/profile-go.
        MaybeProfilerPause(client, server, scenario, condition);

        scenario.Setup(server, client);

        // Reset the visual-smoothness accumulator AFTER Setup so the metric
        // covers only the scenario's observation window — the spawn burst,
        // teleport-to-spawn, and first-tick clock alignment all produce Δv
        // spikes that aren't representative of steady-state smoothness.
        if ((scenario.ApplicableMetrics & MetricKey.M14) != 0)
        {
            try { using var _ = client.Send(new { cmd = "visual-smoothness-reset" }); }
            catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] visual-smoothness-reset failed: {ex.Message}"); }
        }

        scenario.Run(server, client, metrics);

        // Snapshot bandwidth + missed-input at the end of the observation
        // window. For S5 (multi-client) we use the observer's counters since
        // the metric of interest is non-authoritative client behaviour.
        TestProcess subject = client;  // override below for S5
        if (!scenario.RequiresObserver)
        {
            using var doc = client.Send(new { cmd = "mispredict-classification-counts" });
            var d = doc.RootElement.GetProperty("data");
            metrics.SetMispredictTotals(
                externalForce: d.GetProperty("externalForce").GetInt32(),
                physicsNondet: d.GetProperty("physicsNondeterminism").GetInt32(),
                degradedNetwork: d.GetProperty("degradedNetwork").GetInt32());
        }
        try
        {
            // Drain the trailing partial bucket inside the harness, then read
            // the array of per-second kB/s samples accumulated since
            // bandwidth-reset. Each entry covers ~1 wall-second of network
            // activity; the metrics struct stores the distribution and reports
            // P50 + P95.
            using var bDoc = subject.Send(new { cmd = "bandwidth-stats" });
            var b = bDoc.RootElement.GetProperty("data");
            if (b.TryGetProperty("bucketsKBps", out var bucketsEl) && bucketsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var buckets = new List<float>(bucketsEl.GetArrayLength());
                foreach (var v in bucketsEl.EnumerateArray()) buckets.Add(v.GetSingle());
                metrics.AddBandwidthBuckets(buckets);
            }
        }
        catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] bandwidth-stats failed: {ex.Message}"); }
        try
        {
            using var miDoc = subject.Send(new { cmd = "missed-input-count" });
            metrics.SetMissedInputTotal(miDoc.RootElement.GetProperty("data").GetProperty("count").GetInt32());
        }
        catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] missed-input-count failed: {ex.Message}"); }
        try
        {
            using var staDoc = subject.Send(new { cmd = "snap-to-auth-count" });
            metrics.SetSnapToAuthTotal(staDoc.RootElement.GetProperty("data").GetProperty("count").GetInt32());
        }
        catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] snap-to-auth-count failed: {ex.Message}"); }
        // M13 lives on the server (per-tick input-buffer state at apply time),
        // so query the server process even when the rest of the metrics
        // collection targets the observer client in multi-client scenarios.
        try
        {
            using var smiDoc = server.Send(new { cmd = "server-missed-input-total" });
            metrics.SetServerMissedInputTotal(
                smiDoc.RootElement.GetProperty("data").GetProperty("count").GetInt32());
        }
        catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] server-missed-input-total failed: {ex.Message}"); }
        if ((scenario.ApplicableMetrics & MetricKey.M14) != 0)
        {
            try
            {
                using var vsDoc = subject.Send(new { cmd = "visual-smoothness" });
                var v = vsDoc.RootElement.GetProperty("data");
                var samples = new List<float>();
                if (v.TryGetProperty("dvSquared", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    samples.Capacity = arr.GetArrayLength();
                    foreach (var el in arr.EnumerateArray()) samples.Add(el.GetSingle());
                }
                metrics.AddVisualSmoothnessSamples(samples);
                m14DvSquaredSamples = samples;

                // M15 — freeze-frame counter pair. Both numerator and
                // denominator must be present; older harness builds without
                // the new payload fall back to "no contribution" via the
                // TryGetProperty bail-outs below.
                if (v.TryGetProperty("freezeFrames", out var ffEl)
                    && v.TryGetProperty("motionFrames", out var mfEl))
                {
                    metrics.AddFreezeFrameCounters(ffEl.GetInt64(), mfEl.GetInt64());
                }
                // M16 — phase-lag sample list.
                if (v.TryGetProperty("phaseLag", out var plArr) && plArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var phaseLag = new List<float>(plArr.GetArrayLength());
                    foreach (var el in plArr.EnumerateArray()) phaseLag.Add(el.GetSingle());
                    metrics.AddPhaseLagSamples(phaseLag);
                }
                // M17 — render pacing gap counter pair.
                if (v.TryGetProperty("renderGapCount", out var gcEl)
                    && v.TryGetProperty("renderFrameCount", out var fcEl))
                {
                    metrics.AddRenderPacingGapCounters(gcEl.GetInt64(), fcEl.GetInt64());
                }
                // M18 — direction-mismatch counter pair.
                if (v.TryGetProperty("dirMismatchFrames", out var dmEl)
                    && v.TryGetProperty("motionPairFrames", out var mpEl))
                {
                    metrics.AddDirectionMismatchCounters(dmEl.GetInt64(), mpEl.GetInt64());
                }
                // M19 — first-person camera jolt samples. Only the local
                // player smoother contributes; passive props produce empty
                // lists. NaN propagates downstream when the pool is empty.
                if (v.TryGetProperty("cameraJoltDvSquared", out var cjArr)
                    && cjArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var cameraJolt = new List<float>(cjArr.GetArrayLength());
                    foreach (var el in cjArr.EnumerateArray()) cameraJolt.Add(el.GetSingle());
                    metrics.AddCameraJoltSamples(cameraJolt);
                }
            }
            catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] visual-smoothness failed: {ex.Message}"); }
        }

        // Copy the client's MonkeLogger debug log into the artifact dir.
        // CopyProcessLog uses FileShare.ReadWrite so we can grab it while the
        // child process still owns the handle; the per-cell filename embeds
        // scenario + condition so multiple cells don't trample each other.
        if (scenario.CopyDebugLog && !string.IsNullOrEmpty(client.RemoteLogPath)
            && _currentRunArtifactDir != null)
        {
            string targetName = $"{scenario.Id}.{condition.Id}.client.log";
            CopyProcessLog(_currentRunArtifactDir, client.RemoteLogPath, targetName);
        }
        // Also copy the server's debug log so cross-process timelines can be
        // reconstructed (e.g. comparing client's auth-snapshot reception
        // timestamps to the server's send timestamps for the same tick).
        if (scenario.CopyDebugLog && !string.IsNullOrEmpty(server.RemoteLogPath)
            && _currentRunArtifactDir != null)
        {
            string targetName = $"{scenario.Id}.{condition.Id}.server.log";
            CopyProcessLog(_currentRunArtifactDir, server.RemoteLogPath, targetName);
        }

        // Mask out metrics the scenario does NOT exercise — render as NaN in
        // the summary so they show as N/A in the dashboard / strip plots
        // rather than as misleading zeros.
        var summary = metrics.ToSummary(scenario.Id, condition.Id);
        MaskInapplicableMetrics(summary, scenario.ApplicableMetrics);
        return summary;
    }

    /// <summary>Hold before scenario.Setup so a PID-attached profiler can
    /// hook the client. Two modes:
    ///
    /// <list type="bullet">
    /// <item><b>Scripted</b> (MONKENET_TEST_PROFILE_DIR set) — write a
    /// handshake file <c>next.txt</c> in the comm dir with PID + scenario +
    /// condition, then poll for a <c>go</c> file the runner script drops
    /// once <c>dotnet-trace</c> is attached. No fallback timeout; the runner
    /// owns the schedule.</item>
    /// <item><b>Manual</b> (MONKENET_TEST_PROFILE=1 but no comm dir) — print
    /// a copy-paste command and sleep for MONKENET_TEST_PROFILE_PAUSE_MS
    /// (default 20 s), or until a <c>profile-go</c> file appears in the cwd.</item>
    /// </list></summary>
    private static void MaybeProfilerPause(TestProcess client, TestProcess server, IScenario scenario, NetworkCondition condition)
    {
        if (!IsEnvTruthy("MONKENET_TEST_PROFILE")) return;

        int clientPid = client.RemotePid;
        int serverPid = server.RemotePid;
        string commDir = System.Environment.GetEnvironmentVariable("MONKENET_TEST_PROFILE_DIR");

        if (!string.IsNullOrEmpty(commDir))
        {
            ScriptedProfilerPause(commDir, clientPid, serverPid, scenario, condition);
            return;
        }

        ManualProfilerPause(clientPid, serverPid, scenario, condition);
    }

    private static void ScriptedProfilerPause(string commDir, int clientPid, int serverPid, IScenario scenario, NetworkCondition condition)
    {
        try { System.IO.Directory.CreateDirectory(commDir); } catch { /* best effort */ }
        string nextFile = System.IO.Path.Combine(commDir, "next.txt");
        string goFile   = System.IO.Path.Combine(commDir, "go");

        // Defensive: clear any stale go from a previous cell so we wait for
        // the runner to drop a fresh one *for this cell*.
        try { if (System.IO.File.Exists(goFile)) System.IO.File.Delete(goFile); } catch { }

        // Hand both PIDs + cell identity to the runner script. Four lines so
        // PowerShell's `Get-Content` reads each part on its own line:
        //   line 0 = client pid
        //   line 1 = server pid
        //   line 2 = scenario id
        //   line 3 = condition id
        try
        {
            System.IO.File.WriteAllText(nextFile,
                $"{clientPid}\n{serverPid}\n{scenario.Id}\n{condition.Id}\n");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[PROFILE] failed to write next.txt: {ex.Message} — falling back to manual mode");
            ManualProfilerPause(clientPid, serverPid, scenario, condition);
            return;
        }

        GD.PrintErr($"[PROFILE] cell handshake → client={clientPid} server={serverPid} {scenario.Id} × {condition.Id} (waiting for runner to attach dotnet-trace)");

        // Bounded wait so a runner crash doesn't hang the suite forever.
        // 5 min is more than enough to launch dotnet-trace even on a slow box.
        var deadline = DateTime.UtcNow.AddMilliseconds(300_000);
        while (DateTime.UtcNow < deadline)
        {
            if (System.IO.File.Exists(goFile))
            {
                try { System.IO.File.Delete(goFile); } catch { }
                GD.PrintErr($"[PROFILE] runner signalled go — resuming {scenario.Id} × {condition.Id}");
                return;
            }
            System.Threading.Thread.Sleep(100);
        }
        GD.PrintErr($"[PROFILE] timed out waiting for runner go signal — resuming without attached trace");
    }

    private static void ManualProfilerPause(int clientPid, int serverPid, IScenario scenario, NetworkCondition condition)
    {
        int pauseMs = 20_000;
        var raw = System.Environment.GetEnvironmentVariable("MONKENET_TEST_PROFILE_PAUSE_MS");
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed) && parsed > 0)
            pauseMs = parsed;

        string traceClient = $"profile-{scenario.Id}.{condition.Id}.client.nettrace";
        string traceServer = $"profile-{scenario.Id}.{condition.Id}.server.nettrace";
        string triggerPath = System.IO.Path.Combine(System.Environment.CurrentDirectory, "profile-go");

        GD.PrintErr("");
        GD.PrintErr("================================================================");
        GD.PrintErr($"[PROFILE] paused before {scenario.Id} × {condition.Id} setup");
        GD.PrintErr($"[PROFILE] client PID = {clientPid}, server PID = {serverPid}");
        GD.PrintErr($"[PROFILE] in another terminal, run (one per process):");
        GD.PrintErr($"[PROFILE]   dotnet-trace collect --process-id {clientPid} --duration 00:00:18 -o {traceClient}");
        GD.PrintErr($"[PROFILE]   dotnet-trace collect --process-id {serverPid} --duration 00:00:18 -o {traceServer}");
        GD.PrintErr($"[PROFILE] then EITHER touch '{triggerPath}' to continue immediately,");
        GD.PrintErr($"[PROFILE] OR wait up to {pauseMs} ms for the pause to lapse.");
        GD.PrintErr("================================================================");

        var deadline = DateTime.UtcNow.AddMilliseconds(pauseMs);
        while (DateTime.UtcNow < deadline)
        {
            if (System.IO.File.Exists(triggerPath))
            {
                try { System.IO.File.Delete(triggerPath); } catch { }
                GD.PrintErr("[PROFILE] trigger file detected — continuing now.");
                break;
            }
            System.Threading.Thread.Sleep(250);
        }
        GD.PrintErr($"[PROFILE] resuming — open '{traceClient}' / '{traceServer}' in https://www.speedscope.app when done.");
    }

    private static bool IsEnvTruthy(string name)
    {
        var v = System.Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) return false;
        v = v.Trim().ToLowerInvariant();
        return v == "1" || v == "true" || v == "yes" || v == "on";
    }

    /// <summary>Replace metrics outside <paramref name="applicable"/> with
    /// <c>float.NaN</c> / sentinel values so downstream artifact writers
    /// render them as N/A. Called per cell once the summary is built.</summary>
    private static void MaskInapplicableMetrics(SyncMetrics.Summary s, MetricKey applicable)
    {
        if ((applicable & MetricKey.M1) == 0)     s.M1_ClockConvergenceTicks = float.NaN;
        if ((applicable & MetricKey.M2) == 0)     s.M2_ClockSteadyStateRmsTicks = float.NaN;
        if ((applicable & MetricKey.M3b) == 0)    s.M3b_ExternalForceRatePct = float.NaN;
        if ((applicable & MetricKey.M4) == 0)     { s.M4_RollbackDepthP50 = float.NaN; s.M4_RollbackDepthP95 = float.NaN; s.M4_RollbackDepthP99 = float.NaN; }
        if ((applicable & MetricKey.M5_rms) == 0) s.M5_PositionErrorRms = float.NaN;
        if ((applicable & MetricKey.M5_p95) == 0) s.M5_PositionErrorP95 = float.NaN;
        if ((applicable & MetricKey.M6) == 0)     s.M6_VisualSmoothRatio = float.NaN;
        if ((applicable & MetricKey.M7) == 0)     s.M7_PostRollbackConvergenceP95 = float.NaN;
        if ((applicable & MetricKey.M9) == 0)     s.M9_MissedInputRatePct = float.NaN;
        if ((applicable & MetricKey.M10) == 0)    { s.M10_BandwidthP50KBps = float.NaN; s.M10_BandwidthP95KBps = float.NaN; }
        if ((applicable & MetricKey.M11) == 0)    s.M11_SnapToAuthRatePct = float.NaN;
        if ((applicable & MetricKey.M13) == 0)    s.M13_ServerMissedInputRatePct = float.NaN;
        if ((applicable & MetricKey.M14) == 0)
        {
            s.M14_VisualSmoothnessRmsDeltaV = float.NaN;
            s.M14_VisualSmoothnessP50DeltaV = float.NaN;
            s.M14_VisualSmoothnessP95DeltaV = float.NaN;
        }
        if ((applicable & MetricKey.M19) == 0)
        {
            s.M19_CameraJoltRmsDeltaV = float.NaN;
            s.M19_CameraJoltP50DeltaV = float.NaN;
            s.M19_CameraJoltP95DeltaV = float.NaN;
            s.M19_CameraJoltP99DeltaV = float.NaN;
        }
    }

    /// <summary>Poll clock-state from both processes and record the gap. Used
    /// at the start of every cell to measure M1/M2 from a cold start. Also
    /// emits one-line GD.Print at start + end of sampling so a transcript shows
    /// the actual gap values for tuning convergence thresholds.</summary>
    private static void SampleClockConvergence(TestProcess server, TestProcess client,
        SyncMetrics metrics, int samples, int intervalMs)
    {
        int firstGap = int.MinValue, lastGap = int.MinValue, minAbs = int.MaxValue, maxAbs = 0;
        for (int i = 0; i < samples; i++)
        {
            try
            {
                using var sDoc = server.Send(new { cmd = "clock-state" });
                using var cDoc = client.Send(new { cmd = "clock-state" });
                int serverTick = sDoc.RootElement.GetProperty("data").GetProperty("serverTick").GetInt32();
                int syncedTick = cDoc.RootElement.GetProperty("data").GetProperty("syncedTick").GetInt32();
                int latency = cDoc.RootElement.GetProperty("data").GetProperty("averageLatencyTicks").GetInt32();
                int gap = syncedTick - serverTick - latency;
                if (firstGap == int.MinValue) firstGap = gap;
                lastGap = gap;
                int abs = Math.Abs(gap);
                if (abs < minAbs) minAbs = abs;
                if (abs > maxAbs) maxAbs = abs;
                metrics.RecordClockSample(gap);
            }
            catch { /* harness not ready yet; continue sampling */ }
            System.Threading.Thread.Sleep(intervalMs);
        }
        GD.Print($"[clock-sample] N={samples} firstGap={firstGap} lastGap={lastGap} |gap| min={minAbs} max={maxAbs}");
    }

    // Stable colour assignment per condition so multiple metric strips
    // colour-match — i.e. C0 is the same blue across every strip.
    private static readonly Dictionary<string, string> ConditionColors = new()
    {
        ["C0"]        = "#1f77b4",   // blue
        ["C1"]        = "#2ca02c",   // green
        ["C2"]        = "#ff7f0e",   // orange
        ["C3"]        = "#d62728",   // red
        ["C4"]        = "#9467bd",   // purple
        ["C5"]        = "#8c564b",   // brown
        ["C2-GoodBroadband"] = "#ff7f0e",
        ["CJITTER"]   = "#17becf",   // cyan — isolated-jitter condition (NetworkCondition.C_Jitter)
    };

    private static string ColorFor(string conditionId) =>
        ConditionColors.TryGetValue(conditionId, out var c) ? c : "#7f7f7f";

    /// <summary>Canonical per-metric spec used by both the dashboard heatmap
    /// and the per-scenario strip plots. Defined once so the two artifacts
    /// stay aligned on thresholds, units, and descriptions.</summary>
    private static readonly StripPlot.MetricSpec[] CanonicalMetrics =
    {
        new StripPlot.MetricSpec { Name = "M1 clock conv",   Unit = "ticks", Threshold = 60f,   AxisMax = 120f,
            Description = "Clock convergence time — ticks until 10 consecutive |gap| < 2-tick samples. Target ≤ 60 (1 s @ 60 Hz). FAIL = no convergence detected." },
        new StripPlot.MetricSpec { Name = "M2 clock RMS",    Unit = "ticks", Threshold = 1.5f,  AxisMax = 5f,
            Description = "Steady-state clock RMS. Reflects underlying network jitter; physically floors at jitterMs / 16.67." },
        new StripPlot.MetricSpec { Name = "M3b ext-force",   Unit = "%",     Threshold = 5f,    AxisMax = 20f,
            Description = "User-visible mispredict rate — server impulses or remote-player collisions the client failed to predict. < 5 % matches Gaffer's 90 % ballistic-accuracy reference." },
        new StripPlot.MetricSpec { Name = "M4 rollback P99", Unit = "ticks", Threshold = 7f,    AxisMax = 60f,
            Description = "99th-percentile rollback depth. GGPO disconnects peers exceeding 7 frames; MonkeNet's 120-tick buffer handles deeper rollbacks but they're visible." },
        new StripPlot.MetricSpec { Name = "M5 pos RMS",      Unit = "m",     Threshold = 0.1f,  AxisMax = 1.0f,
            Description = "Pre-reconcile position error RMS. Matches Gaffer's 0.1 m no-correction floor; above this corrections become visible to the player." },
        new StripPlot.MetricSpec { Name = "M5 pos P95",      Unit = "m",     Threshold = 1.0f,  AxisMax = 5.0f,
            Description = "Tail position error. Must stay below Gaffer's 2 m hard snap-to threshold; P95 captures occasional outliers under bad networks." },
        new StripPlot.MetricSpec { Name = "M6 visual ratio", Unit = "ratio", Threshold = 0.6f,  AxisMax = 1.5f,
            Description = "Visual smoothing effectiveness — mean(visual err) / mean(body err). < 0.6 = smoother cuts perceived error by > 40 %; ~1.0 in steady state (no body error to smooth)." },
        new StripPlot.MetricSpec { Name = "M7 post-RB conv", Unit = "ticks", Threshold = 7f,    AxisMax = 30f,
            Description = "Ticks to recover < 0.1 m post-rollback. Only meaningful in S3 impulse-response where exactly one external force is applied at a known tick." },
        new StripPlot.MetricSpec { Name = "M9 missed input", Unit = "evt",   Threshold = 10f,   AxisMax = 60f,
            Description = "Cumulative count of (tick × entity) events where the predictor had to fall back to default input because no cached server input was available for a remote entity that previously HAD input." },
        new StripPlot.MetricSpec { Name = "M10 bw P50",      Unit = "kB/s",  Threshold = 5f,    AxisMax = 30f,
            Description = "Median per-second sent + received bytes / second across the scenario window. Industry-tuned games target 2–5 kB/s steady-state; unoptimised replication is 30–50 kB/s. P50 captures the typical-tick cost; pair with P95 for tail bursts (e.g. spawn flood)." },
        new StripPlot.MetricSpec { Name = "M10 bw P95",      Unit = "kB/s",  Threshold = 15f,   AxisMax = 60f,
            Description = "95th-percentile per-second bandwidth. Captures the spawn burst and any one-off snapshot floods. P95 above the threshold means the server occasionally floods the client with state — usually fine, but a steady-high P95 with a normal P50 indicates bursty replication that could be batched or throttled." },
        new StripPlot.MetricSpec { Name = "M11 snap-to-auth",Unit = "%",     Threshold = 10f,   AxisMax = 80f,
            Description = "Rate of snapshots that arrived too old to resim (depth > MaxRollbackTicks) and were corrected by teleport-snap instead. At low-latency conditions this should be ~0; high values at C3/C4 are expected with a tight cap. Distinct from M3 (rollback mispredicts): M3 measures prediction quality, M11 measures how often the cap is binding." },
        new StripPlot.MetricSpec { Name = "M13 srv miss input",Unit = "%",     Threshold = 5f,    AxisMax = 30f,
            Description = "Server-side missed-input rate: (entity × tick) events where the server ticked a client-owned entity without finding a fresh client-stamped input and fell back to repeat-stale / default, divided by observation entity-ticks. Direct quality signal for the input-arrival pipeline. The pre-drive warm-up (entity exists on the server but the client hasn't started driving yet) is excluded — only ticks AFTER the first received input from that entity count. Threshold 5 %: with the InputDelayTicks auto-adjuster on, S7-MultiBodyChaos settles at 0 % across most conditions; C2 (where jitter > InputDelayTicks's steady-state target) shows ~15 %. Distinct from M9 (client-side replay missed an input in snapshot history): M9 is a replay-time event at the client; M13 is an apply-time event at the server." },
        new StripPlot.MetricSpec { Name = "M14 vis smoothness RMS", Unit = "m/s", Threshold = 1.0f, AxisMax = 5f,
            Description = "Visual smoothness — RMS of per-render-frame |Δv| on the visual mesh (m/s). Δv is the change in mesh world-space velocity between consecutive render frames; constant-velocity motion reports 0, jitter / snaps / direction flips raise it. RMS over-weights tail spikes — pair with the p50 column to see the TYPICAL frame's smoothness. Directly measures the user-perceived smoothness goal that M5 / M6 do not capture (those measure auth-error, not frame-to-frame discontinuity)." },
        new StripPlot.MetricSpec { Name = "M14 vis smoothness P50", Unit = "m/s", Threshold = 0.3f, AxisMax = 2f,
            Description = "Median per-render-frame |Δv| (m/s). The TYPICAL-frame smoothness measure — unlike RMS, p50 is insensitive to occasional snap-overflow spikes, so a low p50 with a high RMS means motion is smooth most of the time but punctuated by visible discontinuities. Reading both together identifies whether degradation is steady jitter (p50 rises) or sparse snaps (RMS rises but p50 stays low)." },
    };

    /// <summary>One-line metric-name + axis-key mapping used to project a
    /// scenario's MetricKey mask down to the columns of CanonicalMetrics.</summary>
    private static readonly MetricKey[] CanonicalMetricKeys =
    {
        MetricKey.M1, MetricKey.M2, MetricKey.M3b, MetricKey.M4,
        MetricKey.M5_rms, MetricKey.M5_p95, MetricKey.M6, MetricKey.M7,
        MetricKey.M9, MetricKey.M10, MetricKey.M10, MetricKey.M11, MetricKey.M13,
        MetricKey.M14, MetricKey.M14,
    };

    private static void WriteDashboard(string path,
        Dictionary<string, List<SyncMetrics.Summary>> rowsByScenario)
    {
        // Heatmap rows = (scenario × condition). Columns = the canonical 10
        // metrics. Each scenario's applicable-mask hides nothing here — the
        // dashboard SHOWS all 10 columns for every row so cross-scenario
        // comparisons stay column-aligned; N/A cells render greyed-out.
        var axes = new QuantitativeDashboard.AxisSpec[CanonicalMetrics.Length];
        for (int i = 0; i < CanonicalMetrics.Length; i++)
        {
            var m = CanonicalMetrics[i];
            axes[i] = new QuantitativeDashboard.AxisSpec
            {
                Name = m.Name,
                Unit = m.Unit,
                Threshold = m.Threshold,
                Description = m.Description,
            };
        }

        var dashboard = new QuantitativeDashboard(
            title: "MonkeNet quantitative-suite dashboard",
            commit: "",
            timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm UTC"),
            axes: axes);

        foreach (var (scenarioId, rows) in rowsByScenario)
        {
            foreach (var r in rows)
            {
                dashboard.AddRow(scenarioId, r.Condition, BuildValueRow(r));
            }
        }
        dashboard.Save(path);
        GD.Print($"[QuantitativeTestBase] dashboard written → {path}");
    }

    private static float[] BuildValueRow(SyncMetrics.Summary r) => new[]
    {
        r.M1_ClockConvergenceTicks, r.M2_ClockSteadyStateRmsTicks,
        r.M3b_ExternalForceRatePct, r.M4_RollbackDepthP99,
        r.M5_PositionErrorRms, r.M5_PositionErrorP95,
        r.M6_VisualSmoothRatio, r.M7_PostRollbackConvergenceP95,
        r.M9_MissedInputRatePct, r.M10_BandwidthP50KBps, r.M10_BandwidthP95KBps,
        r.M11_SnapToAuthRatePct, r.M13_ServerMissedInputRatePct,
        r.M14_VisualSmoothnessRmsDeltaV, r.M14_VisualSmoothnessP50DeltaV,
    };

    /// <summary>Per-scenario strip plot. Metrics outside the scenario's
    /// applicability mask are filtered OUT entirely — the SVG only shows the
    /// strips the scenario actually exercises. With ~3 conditions per scenario
    /// and 3–6 applicable metrics, the plot is compact and metric-focused.</summary>
    private static void WriteScenarioStripPlot(string path, string scenarioId,
        MetricKey applicable, List<SyncMetrics.Summary> rows)
    {
        // Project the canonical 10 down to just the applicable subset.
        var filteredMetrics = new List<StripPlot.MetricSpec>();
        var includedIdx = new List<int>();
        for (int i = 0; i < CanonicalMetrics.Length; i++)
        {
            if ((applicable & CanonicalMetricKeys[i]) != 0)
            {
                filteredMetrics.Add(CanonicalMetrics[i]);
                includedIdx.Add(i);
            }
        }
        if (filteredMetrics.Count == 0) return;

        var plot = new StripPlot(
            title: $"{scenarioId} — metric strip plot",
            subtitle: $"{rows.Count} condition(s); irrelevant metrics hidden",
            metrics: filteredMetrics.ToArray());

        foreach (var r in rows)
        {
            var fullRow = BuildValueRow(r);
            var values = new float[includedIdx.Count];
            for (int j = 0; j < includedIdx.Count; j++) values[j] = fullRow[includedIdx[j]];
            plot.AddCell(new StripPlot.Cell
            {
                Scenario = scenarioId,
                Condition = r.Condition,
                ConditionColor = ColorFor(r.Condition),
                Values = values,
            });
        }
        plot.Save(path);
    }

    /// <summary>Per-scenario CDF plot of |Δv| (m/s) for the M14 visual-
    /// smoothness metric. One curve per condition, plotted on a log x-axis
    /// so both the smooth tail (≈0.01 m/s) and the snap-overflow spikes
    /// (≈10 m/s) fit on one plot without compressing the typical-frame
    /// region to invisibility. Y axis is cumulative fraction [0,1] —
    /// horizontal lines at 0.5 / 0.95 mark p50 / p95 read-off points,
    /// matching the columns reported in the strip plot.
    ///
    /// <para>Why CDF over a histogram: percentile lines are easier to read
    /// off a CDF, the curves overlay cleanly without binning artifacts,
    /// and the visual answer to "is C3's right shoulder a hard tail or a
    /// uniform shift" is obvious from the curve shape.</para></summary>
    private static void WriteVisualSmoothnessCdf(string path, string scenarioId,
        Dictionary<string, IReadOnlyList<float>> perConditionSamples)
    {
        // SVG layout — single panel, hard-coded so the writer stays self-
        // contained and we don't have to retrofit the SvgPlot framework for
        // log-axis support just for this one plot.
        const int W = 1000;
        const int H = 560;
        const int LeftPad = 80;
        const int RightPad = 240;
        const int TopPad = 60;
        const int BottomPad = 70;
        int plotW = W - LeftPad - RightPad;
        int plotH = H - TopPad - BottomPad;

        // Log x-axis bounds: cover [0.001, 1000] m/s in decade ticks. The
        // metric is per-render-frame |Δv| so values span ~3 decades in
        // practice (clean steady-state ≈ 0.01, snap-overflow ≈ 10–50).
        const double XMin = 0.001;
        const double XMax = 1000.0;
        double logMin = Math.Log10(XMin);
        double logMax = Math.Log10(XMax);

        double XToPx(double xValue)
        {
            if (xValue <= 0) xValue = XMin;
            double clamped = Math.Max(XMin, Math.Min(XMax, xValue));
            return LeftPad + plotW * (Math.Log10(clamped) - logMin) / (logMax - logMin);
        }
        double YToPx(double cdf) => TopPad + plotH * (1.0 - cdf);

        var ci = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append(ci, $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {W} {H}' width='{W}' height='{H}' font-family='monospace' font-size='12'>");
        sb.Append("<rect width='100%' height='100%' fill='white'/>");

        // Title
        sb.Append(ci, $"<text x='{W / 2}' y='24' text-anchor='middle' font-size='15' font-weight='bold'>{scenarioId} — M14 visual smoothness CDF</text>");
        sb.Append(ci, $"<text x='{W / 2}' y='42' text-anchor='middle' fill='#555' font-size='11'>per-render-frame |Δv| (m/s), log x; one curve per condition</text>");

        // Plot frame
        sb.Append(ci, $"<rect x='{LeftPad}' y='{TopPad}' width='{plotW}' height='{plotH}' fill='none' stroke='#333'/>");

        // X-axis decade ticks + labels
        for (int dec = (int)logMin; dec <= (int)logMax; dec++)
        {
            double x = XToPx(Math.Pow(10, dec));
            sb.Append(ci, $"<line x1='{x:0.#}' y1='{TopPad + plotH}' x2='{x:0.#}' y2='{TopPad + plotH + 5}' stroke='#333'/>");
            sb.Append(ci, $"<line x1='{x:0.#}' y1='{TopPad}' x2='{x:0.#}' y2='{TopPad + plotH}' stroke='#eee'/>");
            string label = dec switch { 0 => "1", 1 => "10", 2 => "100", 3 => "1k", -1 => "0.1", -2 => "0.01", -3 => "0.001", _ => $"1e{dec}" };
            sb.Append(ci, $"<text x='{x:0.#}' y='{TopPad + plotH + 18}' text-anchor='middle'>{label}</text>");
        }
        sb.Append(ci, $"<text x='{LeftPad + plotW / 2}' y='{TopPad + plotH + 42}' text-anchor='middle' font-size='12'>|Δv| (m/s)</text>");

        // Y-axis ticks + labels + percentile guide lines at 0.5 / 0.95
        for (int i = 0; i <= 10; i++)
        {
            double cdf = i / 10.0;
            double y = YToPx(cdf);
            sb.Append(ci, $"<line x1='{LeftPad - 5}' y1='{y:0.#}' x2='{LeftPad}' y2='{y:0.#}' stroke='#333'/>");
            sb.Append(ci, $"<line x1='{LeftPad}' y1='{y:0.#}' x2='{LeftPad + plotW}' y2='{y:0.#}' stroke='#eee'/>");
            sb.Append(ci, $"<text x='{LeftPad - 8}' y='{y + 4:0.#}' text-anchor='end'>{cdf:0.0}</text>");
        }
        // Emphasize p50 and p95 guide lines so a reader can drop verticals
        // down to the x-axis to read those percentiles per curve.
        foreach (var (cdf, label) in new[] { (0.5, "p50"), (0.95, "p95") })
        {
            double y = YToPx(cdf);
            sb.Append(ci, $"<line x1='{LeftPad}' y1='{y:0.#}' x2='{LeftPad + plotW}' y2='{y:0.#}' stroke='#999' stroke-dasharray='4,3'/>");
            sb.Append(ci, $"<text x='{LeftPad + plotW + 4}' y='{y - 3:0.#}' fill='#666' font-size='10'>{label}</text>");
        }
        sb.Append(ci, $"<text x='{LeftPad - 50}' y='{TopPad + plotH / 2}' text-anchor='middle' font-size='12' transform='rotate(-90 {LeftPad - 50},{TopPad + plotH / 2})'>cumulative fraction</text>");

        // One CDF per condition, in the canonical condition order so colours
        // stay consistent with the strip plots even when a scenario only
        // exercised a subset of the conditions.
        var orderedConditionIds = new List<string>();
        foreach (var key in new[] { "C0", "C1", "C2", "C2-GoodBroadband", "C3", "C4", "C5", "CJITTER" })
            if (perConditionSamples.ContainsKey(key)) orderedConditionIds.Add(key);
        foreach (var key in perConditionSamples.Keys)
            if (!orderedConditionIds.Contains(key)) orderedConditionIds.Add(key);

        int legendY = TopPad + 8;
        foreach (var conditionId in orderedConditionIds)
        {
            var dvSquared = perConditionSamples[conditionId];
            if (dvSquared.Count == 0) continue;
            // Sort the per-frame |Δv| values (m/s) so the empirical CDF is a
            // monotonic curve. sqrt converts the stored |Δv|² back to |Δv|
            // for plotting on the m/s x-axis.
            var dv = new float[dvSquared.Count];
            for (int i = 0; i < dvSquared.Count; i++) dv[i] = (float)Math.Sqrt(Math.Max(0, dvSquared[i]));
            Array.Sort(dv);
            int n = dv.Length;
            string color = ColorFor(conditionId);

            // Sub-sample to at most ~400 polyline vertices — at 60 fps × 8 s
            // ≈ 480 samples we'd otherwise emit one path point per sample,
            // which is fine but wasteful. Keep boundary points exact so the
            // p50/p95 readings off the plot are accurate.
            int stride = Math.Max(1, n / 400);
            sb.Append(ci, $"<polyline fill='none' stroke='{color}' stroke-width='1.5' points='");
            sb.Append(ci, $"{XToPx(dv[0]):0.#},{YToPx(0):0.#} ");
            for (int i = 0; i < n; i += stride)
            {
                double cdf = (i + 1) / (double)n;
                sb.Append(ci, $"{XToPx(dv[i]):0.#},{YToPx(cdf):0.#} ");
            }
            sb.Append(ci, $"{XToPx(dv[n - 1]):0.#},{YToPx(1):0.#}");
            sb.Append("'/>");

            // Legend entry — include the per-condition p50 / p95 / RMS in
            // the legend so the reader doesn't have to cross-reference the
            // strip plot or CSV to put a number on each curve.
            double sumSq = 0;
            foreach (var v in dvSquared) sumSq += v;
            double rms = Math.Sqrt(sumSq / dvSquared.Count);
            double p50 = dv[Math.Min(n - 1, (int)Math.Ceiling(0.50 * n) - 1)];
            double p95 = dv[Math.Min(n - 1, (int)Math.Ceiling(0.95 * n) - 1)];
            int lx = LeftPad + plotW + 30;
            sb.Append(ci, $"<line x1='{lx}' y1='{legendY}' x2='{lx + 18}' y2='{legendY}' stroke='{color}' stroke-width='2'/>");
            sb.Append(ci, $"<text x='{lx + 24}' y='{legendY + 4}' font-size='11'>{conditionId}</text>");
            sb.Append(ci, $"<text x='{lx}' y='{legendY + 18}' font-size='9' fill='#555'>p50={p50:0.###} p95={p95:0.##} RMS={rms:0.##}</text>");
            legendY += 36;
        }

        sb.Append("</svg>");
        try
        {
            System.IO.File.WriteAllText(path, sb.ToString());
            GD.Print($"[QuantitativeTestBase] M14 CDF written → {path}");
        }
        catch (Exception ex) { GD.PrintErr($"[QuantitativeTestBase] M14 CDF write failed: {ex.Message}"); }
    }
}
