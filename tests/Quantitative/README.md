# MonkeNet Quantitative Test Suite

A reproducible benchmark of MonkeNet's prediction + reconciliation quality
across a deliberately-chosen scenario × network-condition matrix. Each cell
spawns a fresh server + client Godot process pair, routes the client through
an in-process [`UdpRelay`](../Infrastructure/UdpRelay.cs) configured with the
cell's latency/loss/jitter, runs a known scenario, and writes one row to a
timestamped summary CSV plus per-scenario plots.

The suite is **read-only verification, not regression gating** — the tests
always pass (modulo crashes). The artifacts are what you inspect:

```
tests/TestResults/Quantitative/<UTC-date>.<commit>[+dirty]/
├── summary.csv                          ← one row per (scenario × condition)
├── dashboard.svg                        ← heatmap of all metrics
├── <ScenarioId>.strip.svg               ← per-scenario applicable-metric strip plot
├── <ScenarioId>.<ConditionId>.mp4       ← per-cell video (only scenarios that opt in)
└── <ScenarioId>.<ConditionId>.client.log
└── <ScenarioId>.<ConditionId>.server.log
```


## Running

```pwsh
# Full matrix — the long one (every scenario × every applicable condition)
powershell -ExecutionPolicy Bypass -File run-tests.ps1 -Worktree "QuantitativeSuite.RunFullMatrix"

# Single S7 cell only (used during the snapback / smoothing exploration)
powershell -ExecutionPolicy Bypass -File run-tests.ps1 -Worktree "S7C4FocusedSuite.RunS7C4"

# S7 across all five conditions (used to characterise per-condition behaviour)
powershell -ExecutionPolicy Bypass -File run-tests.ps1 -Worktree "S7C4FocusedSuite.RunS7AllConditions"
```

The worktree wrapper copies the working tree to a temp dir before building,
which lets multiple invocations run in parallel and avoids stomping on
`.godot/mono/temp/bin/` artifacts. `GODOT_BIN` must point at a debug Godot
build with the Mono module — the test runner short-circuits with a no-op
message if it's unset, matching the rest of the multi-process suite.


## Architecture

```
QuantitativeSuite.RunFullMatrix
        │
        ▼
QuantitativeTestBase.RunMatrix(IScenario[])
        │
        ├── compute per-run artifact folder (date + commit + dirty marker)
        │
        ├── for each scenario × applicable condition:
        │       ├── spawn UdpRelay(latency, jitter, loss)
        │       ├── spawn Godot server process (real ENet port)
        │       ├── spawn Godot client process (connects through relay)
        │       ├── optional second observer client (S5 only)
        │       ├── scenario.Setup(server, client)
        │       ├── scenario.Run(server, client, metrics)
        │       ├── poll mispredict counts, missed-input total, bandwidth
        │       ├── copy client + server debug logs (scenarios that opt in)
        │       └── emit one SyncMetrics.Summary
        │
        ├── write summary.csv via MetricsSummaryCsv
        ├── write dashboard.svg via QuantitativeDashboard
        └── for each scenario:
                └── write <Scenario>.strip.svg via StripPlot
```

Key files:
- [`QuantitativeTestBase.cs`](QuantitativeTestBase.cs) — the runner. Owns the
  per-cell process lifecycle, the canonical metric spec table used by both
  dashboard and strip plots, clock-sync sampling for M1/M2, and the
  applicability-mask projection.
- [`IScenario.cs`](IScenario.cs) — scenario contract: id, condition list,
  metric applicability mask, Setup/Run callbacks, video + log opt-ins.
- [`NetworkCondition.cs`](NetworkCondition.cs) — the condition presets.
- [`MetricKey.cs`](MetricKey.cs) — flag enum + bundled masks scenarios use
  to declare which metrics they exercise.
- [`Scenarios/`](Scenarios/) — one file per scenario.
- [`../Infrastructure/Metrics/SyncMetrics.cs`](../Infrastructure/Metrics/SyncMetrics.cs)
  — metric accumulators + the `Summary` struct serialised into one CSV row.
- [`../Infrastructure/Artifacts/`](../Infrastructure/Artifacts/) — SVG/CSV
  writers (`QuantitativeDashboard`, `StripPlot`, `MetricsSummaryCsv`, etc.).


## Network conditions

Defined in [`NetworkCondition.cs`](NetworkCondition.cs). The `latencyMs`
parameter is **one-way** delivery delay applied by `UdpRelay` to every
packet in each direction; RTT is therefore `2 × latencyMs` plus jitter.
Total network impact on rollback depth ≈ `2·latencyMs + 2·jitterMs +
sendInterval` ticks at 60 Hz (see the [rollback-depth deep-dive](#rollback-depth-formula)
below).

| Id | Label | one-way latency | loss | jitter | clock-sync timeout | matches |
|---|---|---|---|---|---|---|
| C0 | Baseline | 0 ms | 0% | 0 ms | 750 ms | loopback / in-process |
| C1 | LAN | 50 ms | 0% | 0 ms | 900 ms | local network |
| C2 | GoodBroadband | 100 ms | 1% | 10 ms | 1500 ms | typical home broadband |
| C3 | AvgBroadband | 200 ms | 2% | 20 ms | 2700 ms | average internet |
| C4 | Poor | 300 ms | 5% | 30 ms | 4800 ms | bad cell / overseas |
| C5 | Stress | 300 ms | **10%** | 50 ms | 8000 ms | functional-only torture test |
| CJITTER | JitterIsolated | 50 ms | 0% | **50 ms** | 2300 ms | isolated-jitter probe |

`NetworkCondition.All` returns C0..C4 (the five canonical cells); C5 is
S8-only; CJITTER is included only in S2's condition list because M1/M2
sensitivity to jitter is what it's there to probe.

`ClockSyncTimeoutMs` sets a per-condition budget for the initial
`WaitForClockSync` (only used by scenarios that don't opt into M1/M2
sampling — `MaxAbsGapTicks = 5` is the convergence criterion). Higher
latency = looser timeout.


## Metrics

Each metric is implemented in [`SyncMetrics.cs`](../Infrastructure/Metrics/SyncMetrics.cs)
as a recorder method + a `Summary` field. The canonical thresholds and
descriptions live in [`QuantitativeTestBase.CanonicalMetrics`](QuantitativeTestBase.cs)
and feed both the dashboard heatmap and the per-scenario strip plots.

| Key | Summary field | Unit | Threshold | Description |
|---|---|---|---|---|
| **M1** | `M1_ClockConvergenceTicks` | ticks | ≤ 60 (1 s @ 60 Hz) | First tick where 10 consecutive `clockGap` samples are all < 2 ticks. `-1` if never converged. |
| **M2** | `M2_ClockSteadyStateRmsTicks` | ticks | ≤ 1.5 | RMS clock gap after M1 convergence. Physically floors at `jitterMs / 16.67`. |
| **M3** | `M3_MispredictRatePct` | % | informational | `(M3a + M3b + M3c) / observation_ticks`. Aggregate misprediction rate; M3a/b/c partition it by cause. |
| **M3a** | `M3a_PhysicsNondetRatePct` | % | informational | Cross-process Jolt FP drift (small velocity residual, no horizontal impulse). Expected noise floor in physics-heavy scenarios. |
| **M3b** | `M3b_ExternalForceRatePct` | % | ≤ 5 | The user-visible class — server-side impulses / remote-player collisions the client didn't predict. Plan target matches Gaffer's 90 % ballistic-accuracy reference. |
| **M3c** | `M3c_DegradedNetworkRatePct` | % | 0 except S8 | Mispredicts that fired *after* the 120-tick prediction buffer was trimmed by hitting the cap. Should be 0 at C0..C4; non-zero only at C5. |
| **M4** | `M4_RollbackDepthP50/P95/P99` | ticks | P99 ≤ 7 | Distribution of `_predictedStates.Count` at the moment of rollback (the resim's tick depth). Strip plot shows P99 only. GGPO disconnects peers exceeding 7 frames. |
| **M5** | `M5_PositionErrorRms` / `M5_PositionErrorP95` | m | RMS ≤ 0.1, P95 ≤ 1.0 | Pre-reconcile `|predPos − authPos|`. RMS matches Gaffer's 0.1 m no-correction floor; P95 captures the tail before Gaffer's 2 m hard-snap threshold. |
| **M6** | `M6_VisualSmoothRatio` | ratio | ≤ 0.6 | `mean(|visual − auth|) / mean(|body − auth|)`. The smoother's effectiveness — < 0.6 means it cuts perceived error by > 40 %. ~1.0 in steady state where the body isn't being snapped. |
| **M7** | `M7_PostRollbackConvergenceP95` | ticks | ≤ 7 | P95 of "ticks until body error drops below 0.1 m after a rollback". Only meaningful in S3 (single-impulse scenario). |
| **M8** | — | — | — | "Entity-count scaling" — analysed externally by comparing M5 across scenarios with different body counts (S1: 1, S4: 6, S5: ?, S7: 40). Not a CSV column. |
| **M9** | `M9_MissedInputRatePct` | % | ≤ 10 | Cumulative count of `(tick × remote-entity)` events where the predictor had to fall back to a stale cached input because no exact-tick server input was available. Reported as `count / observation_ticks` (so really an "events per tick" rate, not a percentage in the usual sense). |
| **M10** | `M10_BandwidthP50KBps` / `M10_BandwidthP95KBps` | kB/s | P50 ≤ 5, P95 ≤ 15 | Client-side `(sentBytes + recvBytes) / second` sampled into one bucket per wall-second across the entire observation window (the spawn burst is included on purpose — it's part of the real network behaviour). Reported as **P50** (typical-tick cost) plus **P95** (burst tail). Industry-tuned games target 2–5 kB/s steady; unoptimised replication is 30–50 kB/s. Pair the two: P95 well above P50 indicates bursty replication (large spawn flood, snapshot batching) — usually fine, but a steadily-high P95 with a high P50 is a candidate for batching/throttling. The earlier single-sample form averaged total bytes over the whole scenario duration and varied 24× across back-to-back runs of the same condition because the spawn burst dominated short windows; bucketing collapses that noise to ~5 % P50 variance, ~1 % P95 variance. |
| **M11** | `M11_SnapToAuthRatePct` | % | ≤ 10 (informational at C3/C4) | Rate of snapshots that arrived too old to resim (depth > `MaxRollbackTicks`) and were corrected by teleport-snap. Distinct from M3: M3 measures *prediction quality*, M11 measures *how often the cap is binding*. High values at C3/C4 are expected with the default cap; high values at C0/C1 indicate the cap is binding more often than it should. |
| **M13** | `M13_ServerMissedInputRatePct` | % | ≤ 1 | **Server-side** missed-input rate: cumulative count of `(tick × entity)` events where the server ticked without finding a fresh client-stamped input in `_pendingInputs` and fell back to repeat-stale / default, divided by observation ticks. Direct quality signal for the input-arrival pipeline; the EVENT version of "server's input buffer for this client ran empty". **Distinct from M9** — M9 is a *replay-time* event at the client (the predictor couldn't find input X for tick T in snapshot history); M13 is an *apply-time* event at the server (the simulation reached tick T without a fresh stamped input). Drives the `InputDelayTicks` tuning loop (Option C in `ClientInputManager`). Non-zero typically means `InputDelayTicks` is too low relative to current network jitter, or the client isn't keeping up with the server tick rate. |

A scenario opts in to a subset via `IScenario.ApplicableMetrics`; metrics
outside the mask render as N/A in the dashboard and are filtered out of the
strip plots so the operator sees only what the scenario actually measures.


## Scenarios

| Id | What it does | Conditions | Metrics opted in | RecordVideo | CopyDebugLog |
|---|---|---|---|---|---|
| **S1-Idle** | One static ball, no input, no movement. Bandwidth baseline + physics-nondeterminism noise floor. | C0 only | `ClockOnly` (just M10 P50/P95) | no | no |
| **S2-LinearMotion** | Rigid-body player walks forward at constant velocity on an empty floor. Reference for prediction-on-predictable-motion. **Sole scenario that samples M1/M2.** | All + CJITTER | `PhysicsBasicWithClock` (M1, M2, M3b, M4, M5, M6, M9, M10 P50/P95) | no | no |
| **S3-ImpulseResponse** | Server applies one known impulse to a passive cube at a deterministic tick. Isolates the external-force class + M7. | C2 only | `All` (M3b, M4, M5, M6, M7, M9, M10 P50/P95) | no | no |
| **S4-PhysicsStack** | Rigid player walks into a 6-cube tower. Physics-interaction quality across the latency sweep. | All | `PhysicsBasic` (no M7) | no | no |
| **S5-MultiClientSharedPhysics** | Server + 2 clients; A drives a player into a shared cube, B observes. Tests non-authoritative client reconciliation. | (single condition — read source) | (read source) | no | no |
| **S7-MultiBodyChaos** | Player walks through a pile of 20 cubes + 20 balls. Stress test for high-entity rollback cost. | C0..C4 | `All` | **yes** | **yes** |
| **S8-DegradationStress** | S4 physics-stack repeated at C5 (300 ms / 10 % loss / 50 ms jitter). Functional torture test — pass criterion is "doesn't crash". | C5 only | `PhysicsBasic` | no | no |

S6_JitterStress was removed; its isolated-jitter probe moved into
`NetworkCondition.C_Jitter` and rolled into S2's condition list.

S7C4FocusedSuite (in this folder, separate `[TestSuite]`) wraps S7 at a
single condition for inner-loop iteration — adds three test cases:
`RunS7C4`, `RunS7C0`, `RunS7AllConditions`. Used during the snapback /
visual-smoothing exploration.

S7PlayerSmoothingPlot is the per-frame visual-vs-body plot generator that
runs out-of-process on a captured S7 log; see the `tools/` directory.


## Artifacts written per run

The runner computes `RunFolderName = <UTC ISO date>.<git commit>[+dirty]`
once at the start of `RunMatrix` and reuses it for every output. Everything
below lives under `tests/TestResults/Quantitative/<run-folder>/`:

- **`summary.csv`** — flat one-row-per-cell CSV. First lines are
  `# commit:` / `# branch:` / `# dirty:` / `# host:` / `# run_utc:` /
  `# rows:` metadata, then a header, then data rows. NaN encodes
  "scenario didn't exercise this metric"; -1 in M1 encodes "no
  convergence detected". Format owned by
  [`MetricsSummaryCsv`](../Infrastructure/Metrics/MetricsSummaryCsv.cs).
- **`dashboard.svg`** — heatmap: rows = `(scenario × condition)`, columns =
  the 10 canonical metric strips. Format owned by
  [`QuantitativeDashboard`](../Infrastructure/Artifacts/QuantitativeDashboard.cs).
- **`<ScenarioId>.strip.svg`** — per-scenario "metric strip plot". One row
  per applicable metric, dots coloured by condition, threshold band shown.
  Inapplicable metrics are filtered out entirely so the SVG only contains
  what the scenario actually exercises. Format owned by
  [`StripPlot`](../Infrastructure/Artifacts/StripPlot.cs).
- **`<ScenarioId>.<ConditionId>.mp4`** — opt-in per-cell video, recorded
  on the client (windowed). Currently S7 is the only opt-in scenario.
- **`<ScenarioId>.<ConditionId>.client.log`** and **`.server.log`** —
  opt-in copies of the `MonkeLogger` debug logs from both processes,
  for post-hoc analysis. Currently S7 is the only opt-in scenario.

Profiler traces (if `MONKENET_TEST_PROFILE=1`) and any custom artifacts
written by scenarios go under the same folder.


## Per-run lifecycle inside RunOneCell

For each `(scenario × condition)` cell `QuantitativeTestBase.RunOneCell`:

1. **Pick a free port** (`NextPort()`), spawn a `UdpRelay` on it
   configured with `(latencyMs, jitterMs, lossRate)`.
2. **Spawn the server process** via `MultiProcessOrchestrator.Spawn("server",
   enetPort: serverPort)`. Wait for `networkReady = true` (30 s timeout).
3. **Spawn the client process** pointed at the relay's listen port (not
   the server's real port). If the scenario opts into `RecordVideo` the
   client is spawned with `recordVideoPath` set and `deferVideoStart = true`
   so the recording window doesn't include the ~5 s of clock-sync warmup.
4. **Spawn an observer client** if `scenario.RequiresObserver`.
5. **Sample M1/M2** if the scenario opts in to `ClockConvergence` — 150
   samples at 35 ms intervals (~5 s window). Otherwise just `WaitForClockSync`
   with `maxGapTicks = 5` and a per-condition timeout.
6. **Arm bandwidth bucketing** via `bandwidth-reset`. Drains ENet's pre-
   window byte counter (warm-up handshake + clock-sync traffic) so the first
   bucket starts clean, then enables 1 Hz sampling on the harness. Each
   render frame after this, the harness pops the destructive byte counter at
   most once per ~1 s and pushes one kB/s value into an in-process bucket
   list. Sampling stays armed across `scenario.Setup` so the spawn burst is
   captured as a tall bucket in the distribution rather than excluded — that
   was a deliberate choice (real network behaviour includes the spawn flood).
7. **Start the recorder** if RecordVideo is on (deferred from step 3).
8. **Optional profiler pause** — `MaybeProfilerPause` if
   `MONKENET_TEST_PROFILE=1`. Writes a handshake file with PIDs to the comm
   directory and waits for the external `dotnet-trace` runner.
9. **`scenario.Setup`** then **`scenario.Run`** — the scenario drives
   itself forward and calls the `SyncMetrics` recorders directly.
10. **Snapshot end-of-window counters** — mispredict totals from the client
    (or observer for S5), missed-input total, and the bandwidth bucket array
    (`bandwidth-stats` drains the trailing partial bucket, disarms sampling,
    and returns `bucketsKBps[]`). `SyncMetrics.AddBandwidthBuckets` stores the
    full array; the summary reports P50 + P95 over it.
11. **Copy debug logs** if `CopyDebugLog` is on.
12. **`MaskInapplicableMetrics`** sets metrics outside the scenario mask
    to NaN / -1 so the summary correctly renders N/A.
13. Return the `SyncMetrics.Summary` row.


## Things the runner does differently from the original plan

The plan that seeded this suite (`give-me-a-suggestion-clever-lantern.md`)
has diverged from the implemented behaviour. The README above reflects the
code; the deltas worth flagging are:

- **S6_JitterStress was removed entirely.** The original plan had a
  dedicated isolated-jitter scenario; it turned out that M1/M2 sensitivity
  to jitter is already captured by S2 + the `NetworkCondition.C_Jitter`
  preset, so the standalone scenario was redundant. The plan's M1/M2
  ownership moved to S2 as a result.
- **M1/M2 are sampled in S2 only.** The plan had them sampled by every
  scenario; the implementation moved to "S2 owns clock-convergence
  metrics for all conditions" because they don't depend on scene contents
  and the per-cell 5 s sampling window cost was wasteful.
- **M3 is partitioned, not single-valued.** The plan had a single M3
  rate; the implementation classifies each mispredict as `physics-
  nondeterminism` / `external-force` / `degraded-network` and exposes the
  per-class rate. The aggregate M3 is still in the CSV but the
  dashboard and strip plots only render M3b (the user-visible class).
- **M8 isn't a CSV column.** It's analysed externally by comparing M5
  across scenarios with different body counts. The plan had a numeric
  M8; the implementation chose the simpler "compare existing rows" path.
- **M11 (snap-to-auth) was added post-plan.** The original plan ended at
  M10. The `MaxRollbackTicks`-overflow path was added to the prediction
  manager *after* the plan and the corresponding metric followed during
  the snapback investigation. M11 is a rate metric (events / observation
  ticks) parallel to M9.
- **M10 was split into P50/P95 post-plan.** The original plan defined M10
  as a single `(sentBytes + recvBytes) / duration` average over the
  observation window. In short scenarios (~14 s) this single sample was
  dominated by the spawn-burst placement and varied 24× across back-to-back
  runs of the same condition (0.43 → 10.18 kB/s for C0). Switched to per-
  wall-second bucketing inside the harness with the runner consuming the
  full array and reporting P50 (typical) + P95 (burst tail). The new
  reading is ~140 kB/s P50 / ~175 kB/s P95 for S7-MultiBodyChaos which
  matches the expected `entity_count × snapshot_hz × per-entity-bytes`
  ballpark for 41 entities; the old reading was undercounting somewhere
  in the single-sample pipeline.
- **The matrix is sparse**, not dense. S1 runs at C0 only, S3 at C2 only,
  S8 at C5 only, and S2 also picks up CJITTER. The plan implied a denser
  matrix; the implementation prunes for runtime cost (~30 s/cell × full
  matrix would be a 20+ minute test).
- **S7 has scenario-specific MP4 + debug-log opt-ins.** No other
  scenario currently sets `RecordVideo` or `CopyDebugLog` true; both
  were added during the S7 visual-smoothing exploration.


## Rollback depth formula

For reference (verified empirically with the
`tools/depth_breakdown.py` analyzer — see `tools/` for the deep-dive
instrumentation added during the snapback investigation):

```
rollback_depth (ticks) ≈ 2·one-way-latency + jitter + sendInterval
```

At 60 Hz tick rate with one-way latency in milliseconds:

| Condition | one-way (ticks) | expected depth (ticks) | observed (S7-C4 trace) |
|---|---|---|---|
| C0 | 0 | ~3-5 | 3-5 |
| C1 | 3 | ~10 | 10 |
| C2 | 6 | ~15 | 14-17 |
| C3 | 12 | ~27 | 27-30 |
| C4 | 18 | ~40 | 39-42 |

`_fixedTickMargin` on `ClientNetworkClock` adds to the forward-prediction
lead (`GetCurrentTick = _currentTick + avgLat + jitter + margin`).
Increasing it makes the client predict further ahead — at the cost of a
proportional increase in rollback depth on every snapshot. Default 0.

Industry libraries land in similar depth bands at the same conditions —
Unity NGO docs cite "~22 frames of re-simulation for a 300 ms connection"
which lines up with C4 at 300 ms RTT (~half MonkeNet's C4 because the test
harness uses 300 ms one-way = 600 ms RTT). Fusion 2 uses an adaptive lead
that shrinks under low jitter; MonkeNet is currently static.
