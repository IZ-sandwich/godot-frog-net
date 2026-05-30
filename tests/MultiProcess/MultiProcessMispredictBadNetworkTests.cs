using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using MonkeNet.Tests.Quantitative;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-MISPREDICT-BADNET-01: Same tower-charge scenario as
/// <see cref="MultiProcessMispredictTests"/>, but with the client and observer
/// connected through a <see cref="UdpRelay"/> injecting simulated bad-network
/// conditions (latency / jitter / packet loss). Produces per-perspective SVG
/// graphs so the reader can see how the prediction + reconcile pipeline copes
/// when packets are delayed / dropped:
///
///   - tower_run_badnet.client.svg      world-Z trace + per-entity divergence
///                                      + cumulative relay packet drops, from
///                                      the active driver client's perspective
///                                      with mispredict markers.
///   - tower_run_badnet.observer.svg    same panels from the passive observer
///                                      client (no input). Observer drift
///                                      under loss isolates "observer can't
///                                      reproduce server motion" from "client
///                                      input mispredicts under its own loss".
///   - tower_run_badnet.client.csv      long-form per-tick per-entity rows.
///   - tower_run_badnet.observer.csv    same shape from observer perspective.
///   - tower_run_badnet.drops.csv       relay packet counters over time.
///   - tower_run_badnet.client.mp4      windowed client video.
///   - tower_run_badnet.observer.mp4    windowed observer video.
///
/// Uses the C4_Poor condition from the quantitative matrix
/// (<see cref="NetworkCondition.C4_Poor"/>: 300 ms latency, 5 % loss, ±30 ms
/// jitter) so the test reuses the same canonical "this is what the wider
/// internet looks like" preset rather than inventing a one-off.
///
/// The assertion budget is intentionally loose — under bad network conditions
/// mispredictions are EXPECTED to spike (snapshot loss → stale auth state →
/// reconcile when it arrives). The purpose of the test is the diagnostic
/// artifacts, not a tight regression bound. We assert only that:
///   1. The scenario completes (server + both clients stay connected).
///   2. The relay actually dropped some packets (sanity check that the
///      injected loss is taking effect — without this an accidentally
///      transparent relay would silently pass with C0-Baseline behaviour).
///   3. The client and observer both end the run with a finite, sub-disaster
///      misprediction count (the loose budget catches "the predictor wedged
///      and is reconciling every tick" rather than gradual drift).
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessMispredictBadNetworkTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "MispredictBadNetwork";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    // Entity types must match MultiProcessMispredictTests (same harness).
    private const byte EntityTypeRigidPlayer = 3;
    private const byte EntityTypeCube = 4;

    // Tower geometry mirrors MultiProcessMispredictTests exactly so a reader
    // diff'ing the two tests sees ONLY the network-conditioning delta, not a
    // re-tuned scenario. Same constants → directly-comparable trace shapes
    // between the baseline and the bad-network runs.
    private const int TowerCubeCount = 6;
    private const float CubeSpacingY = 1.5f;
    private const float TowerBaseY = 0.5f;
    private const float TowerZ = -1.5f;

    private const int SnapshotIntervalTicks = 4;
    private const int RunTicks = 720;
    private const int SnapshotArmTicks = 60;
    private const int TowerSettleTicks = 120;

    // Loose ceiling — the point is to characterise behaviour under bad net,
    // not to enforce a tight bound. Anything below ~RunTicks/2 means the
    // predictor is still recovering after reconciles rather than reconciling
    // every tick, which is what we want to verify.
    private const int MispredictBudgetUnderLoad = 300;

    [TestCase]
    public void MultiProcess_RigidPlayer_RunsIntoTowerWhileJumping_UnderBadNetwork()
    {
        if (Orch == null) return;

        // Pick the canonical "Poor" condition from the quantitative matrix:
        // 300 ms latency, 5 % packet loss, ±30 ms jitter. Latency dominates
        // mispredictions (snapshot is 300 ms stale by the time it arrives);
        // loss adds bursts where the client extrapolates further before the
        // next snapshot lands; jitter scrambles arrival order.
        var condition = NetworkCondition.C4_Poor;

        // Manual triad spawn (cannot use SpawnTriad because that variant
        // wires clients straight to the server port; we need both clients
        // pointed at the relay's listen port so the conditions take effect
        // on BOTH the driver and the observer).
        int serverPort = NextPort();
        var relay = StartRelay(serverPort);
        relay.SetConditions(condition.LatencyMs, condition.JitterMs, condition.LossRate);

        var paths = ArtifactsFor("tower_run_badnet");
        string clientVideo   = paths.Mp4;
        string observerVideo = Path.Combine(paths.Directory, "tower_run_badnet.observer.mp4");

        var server = Orch.Spawn("server", enetPort: serverPort, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        // Both clients connect to relay.ListenPort, not serverPort. The handshake
        // budget is bumped because conditioned-up RTTs (300 ms + jitter + a few
        // dropped retries) can blow past the 30 s default before the ENet
        // handshake completes. 60 s matches QuantitativeTestBase.RunOneCell.
        // Defer recorder construction so the MP4 doesn't capture the ~15 s
        // bad-network clock-sync window — mirrors QuantitativeTestBase.
        var client   = Orch.Spawn("client", enetPort: relay.ListenPort, label: "c1",
            recordVideoPath: clientVideo, deferVideoStart: true);
        var observer = Orch.Spawn("client", enetPort: relay.ListenPort, label: "observer",
            recordVideoPath: observerVideo, deferVideoStart: true);
        client.WaitReady(networkReady: true, timeoutMs: 60_000);
        observer.WaitReady(networkReady: true, timeoutMs: 60_000);

        ServerLogPath = server.RemoteLogPath;
        ClientLogPath = client.RemoteLogPath;
        ObserverLogPath = observer.RemoteLogPath;

        // Match QuantitativeTestBase: canonical ±5-tick gap, with the
        // per-condition timeout (sized to ~p99 × 1.5 of cold-start convergence
        // for this latency / jitter / loss profile).
        WaitForClockSync(server, client,   timeoutMs: condition.ClockSyncTimeoutMs);
        WaitForClockSync(server, observer, timeoutMs: condition.ClockSyncTimeoutMs);

        // Clocks converged — start recording now so the MP4 begins at the
        // scenario action instead of the bad-network warm-up.
        StartDeferredRecording(client, clientVideo);
        StartDeferredRecording(observer, observerVideo);

        int clientNetId = client.NetworkId;
        AssertThat(clientNetId).OverrideFailureMessage("client must have a non-zero ENet peer id").IsNotEqual(0);

        // Park the observer idle — same pattern as SpawnTriad. Without this,
        // the harness's default input is still zeroed but the schedule
        // pipeline doesn't get exercised, which can mask a separate "no-input-
        // schedule observer behaves differently" failure mode.
        observer.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[] { new { tick = observer.ReadClientTick(), moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } },
        });

        // ── Scenario (mirrors MultiProcessMispredictTests.MultiProcess_RigidPlayer_RunsIntoTowerWhileJumping...) ─────
        server.WaitForTicks(SnapshotArmTicks);

        var cubeEids = new List<int>();
        for (int i = 0; i < TowerCubeCount; i++)
        {
            float y = TowerBaseY + i * CubeSpacingY;
            cubeEids.Add(SpawnEntity(server, EntityTypeCube, authority: 0, 0f, y, TowerZ));
        }
        server.WaitForTicks(TowerSettleTicks);

        int playerEid = SpawnEntity(server, EntityTypeRigidPlayer, client.NetworkId, 0f, 0f, 1.5f);

        const int PlayerFallTicks = 90;
        int anchorTick = client.ReadClientTick() + PlayerFallTicks;

        const byte SpaceFlag = 0b_0000_0001;
        var schedule = new List<object>
        {
            new { tick = anchorTick - PlayerFallTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 },
        };
        for (int elapsed = 0; elapsed < RunTicks; elapsed += 20)
        {
            int phase = elapsed / 60;
            int phaseMod = phase % 4;
            float moveY = (phaseMod < 2) ? -1f : +1f;
            bool jump = (elapsed / 20) % 2 == 0 && phaseMod < 2;
            byte keys = jump ? SpaceFlag : (byte)0;
            schedule.Add(new
            {
                tick = anchorTick + elapsed,
                moveX = 0.0,
                moveY = (double)moveY,
                yaw = 0.0,
                keys = (int)keys,
            });
        }
        schedule.Add(new { tick = anchorTick + RunTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        client.WaitForClientTick(anchorTick);
        int baselineMispredictsClient   = ReadMispredictCount(client);
        int baselineMispredictsObserver = ReadMispredictCount(observer);
        long baselineRelayInbound  = relay.TotalInbound;
        long baselineRelayDropIn   = relay.DroppedInbound;
        long baselineRelayOutbound = relay.TotalOutbound;
        long baselineRelayDropOut  = relay.DroppedOutbound;

        // Capture samples from BOTH clients each sample tick. Server samples
        // provide the divergence baseline (server's authoritative pose vs
        // each client's predicted pose at the same tick). The relay counter
        // sample provides the "how many packets did the network swallow
        // between the last sample and this one" data for the drops panel.
        var clientSamples = new List<Sample>();
        var observerSamples = new List<Sample>();
        var serverSamples = new List<Sample>();
        var relaySamples = new List<RelayCounterSample>();
        for (int t = SnapshotIntervalTicks; t <= RunTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = anchorTick + t;
            client.WaitForClientTick(targetTick);
            // observer's synced tick can lag the driver under jitter; the
            // small wait keeps sample triples aligned at the same physics
            // tick across all three processes.
            try { observer.WaitForClientTick(targetTick, timeoutMs: 5_000); }
            catch (TimeoutException)
            {
                // Observer may temporarily fall behind under heavy loss;
                // capture whatever it has now rather than aborting the run.
            }

            clientSamples.Add(CaptureSample(client, targetTick));
            observerSamples.Add(CaptureSample(observer, targetTick));
            serverSamples.Add(CaptureSample(server, targetTick));
            relaySamples.Add(new RelayCounterSample
            {
                Tick = targetTick,
                TotalInbound = relay.TotalInbound - baselineRelayInbound,
                DroppedInbound = relay.DroppedInbound - baselineRelayDropIn,
                TotalOutbound = relay.TotalOutbound - baselineRelayOutbound,
                DroppedOutbound = relay.DroppedOutbound - baselineRelayDropOut,
            });
        }

        int clientMispredicts   = ReadMispredictCount(client)   - baselineMispredictsClient;
        int observerMispredicts = ReadMispredictCount(observer) - baselineMispredictsObserver;
        long finalDropIn  = relay.DroppedInbound  - baselineRelayDropIn;
        long finalDropOut = relay.DroppedOutbound - baselineRelayDropOut;
        long finalTotalIn = relay.TotalInbound    - baselineRelayInbound;
        long finalTotalOut= relay.TotalOutbound   - baselineRelayOutbound;

        // ── Artefacts ─────────────────────────────────────────────────────
        WritePerspectivePlot(paths, "client",   clientSamples,   serverSamples, relaySamples,
            playerEid, cubeEids, baselineMispredictsClient, condition);
        WritePerspectivePlot(paths, "observer", observerSamples, serverSamples, relaySamples,
            playerEid, cubeEids, baselineMispredictsObserver, condition);
        WritePerspectiveCsv(paths, "client",   clientSamples,   playerEid, cubeEids);
        WritePerspectiveCsv(paths, "observer", observerSamples, playerEid, cubeEids);
        WriteDropsCsv(paths, relaySamples);

        GD.Print($"[MP-MISPREDICT-BADNET] {condition} → client mispredicts={clientMispredicts}, " +
            $"observer mispredicts={observerMispredicts}, " +
            $"drops in={finalDropIn}/{finalTotalIn} ({(finalTotalIn == 0 ? 0 : 100.0 * finalDropIn / finalTotalIn):F1}%), " +
            $"drops out={finalDropOut}/{finalTotalOut} ({(finalTotalOut == 0 ? 0 : 100.0 * finalDropOut / finalTotalOut):F1}%)");

        CopyProcessLogs(paths);
        CopyObserverLog(paths, "tower_run_badnet");

        // Sanity: the relay must actually be dropping packets — otherwise the
        // test is effectively running C0_Baseline and the artifacts don't
        // characterise what they claim to. With 5% loss over a 12-second run
        // we'd expect dozens to hundreds of drops in each direction, so
        // anything > 0 is enough to confirm conditions are active.
        AssertThat((int)(finalDropIn + finalDropOut))
            .OverrideFailureMessage(
                $"relay did not drop ANY packets across the run (inbound={finalDropIn}, outbound={finalDropOut}). " +
                $"Either {condition} is misconfigured or the relay isn't wired into the data path.")
            .IsGreater(0);

        AssertThat(clientMispredicts)
            .OverrideFailureMessage(
                $"client mispredicted {clientMispredicts} times in {RunTicks} ticks under {condition} " +
                $"(loose budget {MispredictBudgetUnderLoad}). " +
                $"Above-budget means the predictor is wedged in a tight reconcile loop. " +
                $"Trace + video at TestResults/MispredictBadNetwork/tower_run_badnet.client.{{svg,mp4}}")
            .IsLessEqual(MispredictBudgetUnderLoad);

        AssertThat(observerMispredicts)
            .OverrideFailureMessage(
                $"observer mispredicted {observerMispredicts} times in {RunTicks} ticks under {condition} " +
                $"(loose budget {MispredictBudgetUnderLoad}). " +
                $"Trace + video at TestResults/MispredictBadNetwork/tower_run_badnet.observer.{{svg,mp4}}")
            .IsLessEqual(MispredictBudgetUnderLoad);
    }

    // One four-panel SVG per perspective (client / observer):
    //   Panel 1: world-Z trace of player + each cube (the "where things are"
    //            view) with mispredict markers.
    //   Panel 2: body-position divergence vs server, per entity.
    //   Panel 3: visual-position divergence vs server (what the player sees).
    //   Panel 4: cumulative relay packet drops (inbound + outbound),
    //            same X-axis as the trace so loss bursts visibly line up
    //            with downstream mispredicts in the upper panels.
    private static void WritePerspectivePlot(ArtifactPaths paths, string perspective,
        List<Sample> perspectiveSamples, List<Sample> serverSamples,
        List<RelayCounterSample> relaySamples,
        int playerEid, List<int> cubeEids, int baselineMispredicts,
        NetworkCondition condition)
    {
        string svgPath = Path.Combine(paths.Directory, $"tower_run_badnet.{perspective}.svg");
        var plot = new SvgPlot(
            $"tower_run_badnet — {perspective} perspective — {condition}");

        // Panel 1: world Z trace.
        var zPanel = plot.AddPanel($"world Z (m) — {perspective} predicted pose", yUnits: "m");
        for (int i = 0; i < cubeEids.Count; i++)
        {
            int eid = cubeEids[i];
            string color = SvgPlot.Palette.Series[(i + 1) % SvgPlot.Palette.Series.Length];
            var pts = new List<(int, float)>();
            foreach (var s in perspectiveSamples)
            {
                foreach (var e in s.Entities)
                {
                    if (e.Id == eid) { pts.Add((s.Tick, e.Position.Z)); break; }
                }
            }
            zPanel.AddSeries($"cube{i} eid={eid}", color, pts);
        }
        var playerPts = new List<(int, float)>();
        foreach (var s in perspectiveSamples)
        {
            foreach (var e in s.Entities)
            {
                if (e.Id == playerEid) { playerPts.Add((s.Tick, e.Position.Z)); break; }
            }
        }
        zPanel.AddSeries("player", SvgPlot.Palette.Series[0], playerPts, strokeWidth: 1.8f);

        // Panels 2 & 3: per-entity divergence (server vs this perspective).
        var bodyPanel   = plot.AddPanel($"|server.pos − {perspective}.body.pos| (m)", yUnits: "m");
        var visualPanel = plot.AddPanel($"|server.pos − {perspective}.visual.pos| (m) — what the player sees", yUnits: "m");

        var tracked = new List<(int eid, string label)> { (playerEid, "player") };
        for (int i = 0; i < cubeEids.Count; i++) tracked.Add((cubeEids[i], $"cube{i} eid={cubeEids[i]}"));
        for (int idx = 0; idx < tracked.Count; idx++)
        {
            int eid = tracked[idx].eid;
            string label = tracked[idx].label;
            string color = SvgPlot.Palette.Series[idx % SvgPlot.Palette.Series.Length];
            var bodyDev = new List<(int, float)>();
            var visualDev = new List<(int, float)>();
            for (int i = 0; i < perspectiveSamples.Count && i < serverSamples.Count; i++)
            {
                EntityState sE = null, cE = null;
                foreach (var e in serverSamples[i].Entities) if (e.Id == eid) { sE = e; break; }
                foreach (var e in perspectiveSamples[i].Entities) if (e.Id == eid) { cE = e; break; }
                if (sE == null || cE == null) continue;
                int tick = perspectiveSamples[i].Tick;
                bodyDev.Add((tick, (sE.Position - cE.Position).Length()));
                visualDev.Add((tick, (sE.Position - cE.VisualPosition).Length()));
            }
            bodyPanel.AddSeries(label, color, bodyDev);
            visualPanel.AddSeries(label, color, visualDev);
        }

        // Panel 4: relay packet drops. Inbound = client→server (lost player
        // input); outbound = server→client (lost snapshots). Outbound loss
        // is what visibly drives mispredictions — input loss only affects
        // the missing-input fallback path on the server. Plotted as
        // cumulative count so a flat slope == steady loss rate and a step
        // == a loss burst; eyeballing slope changes against the mispredict
        // markers in the upper panels shows where bursts produce reconciles.
        var dropsPanel = plot.AddPanel("relay packet drops (cumulative)", yUnits: "pkts");
        var dropInbound = new List<(int, float)>();
        var dropOutbound = new List<(int, float)>();
        var totalInbound = new List<(int, float)>();
        var totalOutbound = new List<(int, float)>();
        foreach (var r in relaySamples)
        {
            dropInbound.Add((r.Tick, r.DroppedInbound));
            dropOutbound.Add((r.Tick, r.DroppedOutbound));
            totalInbound.Add((r.Tick, r.TotalInbound));
            totalOutbound.Add((r.Tick, r.TotalOutbound));
        }
        dropsPanel.AddSeries("dropped client→server (input loss)",   SvgPlot.Palette.Series[1], dropInbound,  strokeWidth: 1.8f);
        dropsPanel.AddSeries("dropped server→client (snapshot loss)", SvgPlot.Palette.Series[4], dropOutbound, strokeWidth: 1.8f);
        dropsPanel.AddSeries("total client→server",                   SvgPlot.Palette.Series[9], totalInbound, dashed: true);
        dropsPanel.AddSeries("total server→client",                   SvgPlot.Palette.Series[7], totalOutbound, dashed: true);

        // Mispredict markers — same logic as the baseline MispredictTests.
        int prev = baselineMispredicts;
        foreach (var s in perspectiveSamples)
        {
            if (s.MispredictionsCount > prev) plot.AddVerticalMarker(s.Tick, "mispredict");
            prev = s.MispredictionsCount;
        }

        plot.Save(svgPath);
        GD.Print($"[MP-MISPREDICT-BADNET] wrote {svgPath} ({perspectiveSamples.Count} samples)");
    }

    private static void WritePerspectiveCsv(ArtifactPaths paths, string perspective,
        List<Sample> samples, int playerEid, List<int> cubeEids)
    {
        string csvPath = Path.Combine(paths.Directory, $"tower_run_badnet.{perspective}.csv");
        var tracked = new HashSet<int>(cubeEids) { playerEid };
        CsvWriter.Write(csvPath, samples, e => tracked.Contains(e.Id),
            new CsvWriter.Column("mispredictionsCount", _ => "0"),
            new CsvWriter.Column("eid", e => CsvWriter.I(e.Id)),
            new CsvWriter.Column("etype", e => CsvWriter.I(e.Type)),
            new CsvWriter.Column("x", e => CsvWriter.F(e.Position.X)),
            new CsvWriter.Column("y", e => CsvWriter.F(e.Position.Y)),
            new CsvWriter.Column("z", e => CsvWriter.F(e.Position.Z)),
            new CsvWriter.Column("vx", e => CsvWriter.F(e.Velocity.X)),
            new CsvWriter.Column("vy", e => CsvWriter.F(e.Velocity.Y)),
            new CsvWriter.Column("vz", e => CsvWriter.F(e.Velocity.Z)));
    }

    private static void WriteDropsCsv(ArtifactPaths paths, List<RelayCounterSample> relaySamples)
    {
        string csvPath = Path.Combine(paths.Directory, "tower_run_badnet.drops.csv");
        var sb = new System.Text.StringBuilder();
        sb.Append("tick,t_s,total_in,dropped_in,total_out,dropped_out\n");
        foreach (var r in relaySamples)
        {
            sb.Append(r.Tick).Append(',')
              .Append((r.Tick / 60.0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TotalInbound).Append(',')
              .Append(r.DroppedInbound).Append(',')
              .Append(r.TotalOutbound).Append(',')
              .Append(r.DroppedOutbound).Append('\n');
        }
        File.WriteAllText(csvPath, sb.ToString());
    }

    /// <summary>One row of relay-counter snapshots taken at a sample tick.
    /// Counts are scenario-relative (baselined when the input schedule begins
    /// firing), not lifetime — handshake + clock-sync traffic is excluded.</summary>
    private struct RelayCounterSample
    {
        public int Tick;
        public long TotalInbound;
        public long DroppedInbound;
        public long TotalOutbound;
        public long DroppedOutbound;
    }
}
