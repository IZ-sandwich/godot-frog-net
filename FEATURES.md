# 🐸 Frog-Net Features

This document lists the features provided by the Frog-Net addon (`addons/monke-net/`). Frog-Net is a Godot 4 / C# multiplayer library built around a **client-authoritative-server** architecture: the server runs the authoritative simulation, clients predict locally, and snapshots reconcile any divergence.

For a tour of how the pieces fit together at a code level see [README-quickstart.md](README-quickstart.md).

---

## Authoritative server simulation

- Dedicated, listen-server, and pure-client modes via `MonkeNetManager.CreateServer` / `CreateClient` / `CreateListenServer`.
- Fixed-tick physics loop driven by the addon: Godot's automatic physics step is disabled and the server steps the Jolt physics space manually once per network tick, so the simulation rate is independent of frame rate. With the current implementation,cross-process Jolt is not bit-deterministic (contact-manifold rounding, friction caches, and bounce timing all drift between processes); the prediction / reconcile / two-tier system is built around that fact rather than relying on lockstep determinism.
- Per-tick snapshots of every networked entity's state are broadcast to all connected clients.
- Server-side spawn / despawn API (`ServerManager.Instance.SpawnEntity<T>(entityType, authority)`) with automatic registration on connecting clients.

## Two-tier client-side prediction

Predicted entities pick a `PredictionTier` (what they do steady-state) and an `InterpolationPolicy` (how they transition into Resim on contact), so different entity types can pay different prediction costs:

- **Resim** — classic full-scene rollback + resimulate on every misprediction. Used for locally-owned players, vehicles, and any prop that must feel maximally responsive.
- **Interpolate** — passive props. Default policy is `KinematicInterpolation`: the body is frozen kinematic and pose-driven from snapshot history (the "Photon Fusion 2" pattern), so there is no local Jolt sim, no reconcile snaps, and no smoother offset to decay — just smooth motion derived directly from the snapshot stream. On contact with a Resim entity the body unfreezes and runs Resim for the hysteresis window (default 15 ticks ≈ 250 ms @ 60 Hz); when the window expires it re-freezes and blends back onto the snapshot-interp curve.

Alternative policies (set per entity in the inspector):

- `Hysteresis` — runs local Jolt sim steady-state and blends toward snapshot on misprediction. Same contact-upgrade window as the default. Use when a prop should keep simulating locally even when nobody is touching it.
- `AlwaysPredict` — no tier flip, body is always Resim. Matches the "always-predict-everything" pattern (Netick, lightyear avian_physics) for bodies that must feel maximally responsive at the cost of more rollback work.

## Server reconciliation

- Per-entity misprediction detection over position, linear velocity, and rotation, with per-entity tunable thresholds.
- Rollback is bounded (`MaxRollbackTicks`, default 25 ≈ 416 ms @ 60 Hz) to cap the worst-case resim cost.
- Misprediction classifier separates the three real-world causes (server-side impulses the client didn't see, cross-process Jolt FP nondeterminism, degraded-network input loss) so debugging surfaces show which class is actually dominant.
- First-snapshot-after-spawn is routed through reconciliation but **not** counted as a misprediction, so freshly spawned entities don't pollute the metric.

## Snapshot interpolation for remote entities

Non-locally-owned entities can run as `ClientInterpolatedEntity` instead of `ClientPredictedEntity` — the framework buffers snapshots and the entity implements `HandleStateInterpolation` to lerp between past and future states.

## Visual smoothing

`PredictionVisualSmoothing3D` owns the visual node's render-frame transform end-to-end:

- Absorbs body teleports from reconcile / rollback into an exponentially-decaying visual offset, so a 20 cm rollback snap of the body produces a sub-perceptual visual nudge instead of a jolt.
- Renders the visual via `Lerp(prev, curr, physics_interpolation_fraction)` every `_Process` rather than relying on Godot's `SceneTreeFTI` cache (which caches stale transforms across mid-frame rollback writes).
- Camera smoothing (To be updated)

## Prediction-friendly rigid body wrapper

`PredictionRigidbody3D` mirrors Fish-Net's wrapper of the same name. Forces, impulses, torques, and velocity writes are queued on the wrapper and flushed deterministically to the body once per tick via `Simulate`. This makes resimulation safe: replaying the same `OnProcessTick` calls produces the same body state.

- `Reconcile` restores the body to an authoritative snapshot before resim.
- `SnapToRest` lets idle bodies stay asleep across reconcile, so persistent contact manifolds in cube stacks survive snapshot churn.

## Authority transfer (ownership requests)

Clients can request or release authority over an entity at runtime:

- `ClientManager.Instance.RequestAuthority(entityId)` / `ReleaseAuthority(entityId)`.
- Server consults the entity's `OwnershipPolicy` resource (configured per `EntitySpawnConfiguration`). The policy gates on:
  - **RequireUnowned** — only claim free entities (default).
  - **MaxRequesterDistance** — proximity check in meters.
  - **AllowOwnerRelease** — sticky ownership when false.
- Custom `ServerEntityManager.OwnershipApprover` escape hatch runs after the declarative policy for game-specific logic (e.g. the demo's vehicle proximity prompt).
- Null policy = reject all (secure default — games opt in per entity type).

## Lag compensation

`LagCompensation.Instance.RaycastAtTick(firedAtTick, origin, direction, length, out hit)` rewinds every networked body's translation and rotation to the moment a client fired, then runs a raycast against the past world state. Default 12-tick history (~200 ms @ 60 Hz) covers typical RTT + jitter. Excludable entity IDs let the shooter pass through their own collider.

## Listen-server mode

`CreateListenServer(port)` runs both `ServerManager` and `ClientManager` in the same process with separate `SceneMultiplayer` instances, so a single Godot instance can host and play. The addon handles the awkward parts:

- Separate ENet peers so server/client teardown doesn't cross-fire.
- Automatic collision-layer separation — server bodies and client replicas share one Jolt space, but the addon overrides their layers at spawn time so they don't collide with each other.

## Persistent client identity & reconnect

- Stable client ID stored in `user://` (and overrideable via CLI), so a player who times out can reconnect to the same server-side state.
- Server retains disconnected clients' entities in a reconnect window; matching identity re-binds them.
- `ClientEntityManager.IsAwaitingReconnect` exposes the state for UI ("Reconnect to previous session" button in the demo).

## Connection monitoring

Client-side signals you can wire to UI:

- `NetworkReady`, `ConnectionFailed`, `ConnectionLost`.
- `ServerSilent` / `ServerResponded` — fires when the server stops acknowledging the client (lets you show a "no response" warning before the ENet timeout triggers).
- `LatencyCalculated(latencyTicks, jitterTicks)` — ongoing ping + jitter, sampled in ticks rather than ms so it's tick-rate-agnostic.

## Binary network message serialization

- `IPackableElement` for input and state messages, `IEntityStateData` adds the `EntityId` for per-entity state inside snapshots.
- All implementing structs are auto-discovered by `MessageSerializer` at startup via reflection — no per-message registration call needed.
- Message bodies themselves carry no reflection: every struct hand-writes `WriteBytes` / `ReadBytes`, so each field is a direct typed write/read on `MessageWriter` / `MessageReader` with no per-field `FieldInfo` walk and a fully predictable wire layout. The runtime path still uses `Activator.CreateInstance` to construct an incoming instance from its type tag before calling `ReadBytes`.

## Per-entity input pipeline

- One `InputProducerComponent` per local entity type. Override `GenerateCurrentInput()` to pack your game's input struct.
- `ClientInputManager` calls every active producer once per tick, packs all inputs into a single ENet payload, and reliably delivers it to the server.
- `ServerInputReceiver` buffers inputs per entity per tick so the server's tick handler always has the correct input for the tick it's simulating.

## Built-in ImGui debug overlay

Frog-Net surfaces its internals (tick, mispredict counts, rollback depth, snapshot lag, per-entity tier transitions, ownership state, lag-compensation history) through [ImGui Godot](https://github.com/pkdawson/imgui-godot), which is the addon's only hard dependency. Useful both during development and when reproducing networking issues.

---

## What this fork adds on top of upstream Monke-Net

This fork is built around **networking rigid-body physics with smooth visuals**. The pieces that don't exist (or work very differently) in upstream Monke-Net:

- The two-tier prediction model with `PredictionTier` + `InterpolationPolicy` (Resim / Interpolate / KinematicInterpolation + Hysteresis / AlwaysPredict).
- `PredictionVisualSmoothing3D` and the body-teleport-absorption path that makes reconcile snaps visually invisible.
- `PredictionRigidbody3D` with deterministic per-tick force/impulse flushing and `SnapToRest` for stable stacks.
- `OwnershipPolicy` resources for declarative authority-transfer approval.
- `LagCompensation` with per-tick world rewinds.
- Unified-prediction refactor: the old separate `ClientAuthorityScene` / `ClientDummyScene` pair was collapsed into a single `ClientScene` — every client now predicts every entity locally, and authority only governs whose input drives it.
- Listen-server collision-layer auto-separation so the server and local client can share a Jolt space without colliding with themselves.

Because of the manual physics-step requirement, the fork still depends on [grazianobolla's modified Godot build](https://github.com/grazianobolla/godot/tree/daylily-zeleen/physics_space_step) (linked from the main README). Vanilla Godot 4.x cannot step its physics world manually [yet](https://github.com/godotengine/godot-proposals/issues/2821)).
