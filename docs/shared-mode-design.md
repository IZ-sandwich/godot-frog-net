# Shared Mode — Design Doc

**Status**: design only, no code yet
**Scope**: add a Fusion 2 Shared Authority-style topology alongside the existing
host+client topology, sharing as much of the existing library as possible.

## 1. Motivation and goals

monke-net today is **host topology**: one dedicated server runs the canonical
physics simulation, every client connects to that server via direct ENet, the
server packs snapshots from its own body state, clients predict locally and
reconcile against server snapshots. This is what Photon Fusion 2 calls
**Host mode** and what unreal-engine-style "dedicated server + remote
authority" netcode looks like in general.

This works well for tight competitive games (cheat resistance, deterministic
contact resolution, server-side rewind for hit detection) but is the wrong
shape for several common project profiles:

- **Drop-in / drop-out lobbies** without a persistent dedicated server.
- **Mobile / WebGL / VR / MR** where the user experience expects "tap to join,
  no hosting cost."
- **Co-op chaos / physics-comedy** games where peer disagreement on physics
  outcomes is acceptable and getting the session up fast matters more than
  deterministic resolution.
- **No-hosting-cost open source projects** that don't want to ship a server
  binary and SLA.

The goal of Shared mode is to support these profiles **without abandoning host
mode**. Both topologies must coexist in the same library and the same project
should be able to pick at scene-load time (or even run mixed sessions, see §13).

### Non-goals

- **Cross-peer determinism**. Fusion 2 Shared mode explicitly gives up on
  this; we will too. Determinism stays a host-mode concern.
- **Cheat resistance comparable to host mode**. Shared mode trusts peers to
  not lie about state they own. Optional mitigations are documented (§14)
  but not part of the core contract.
- **Replacing host mode**. Host mode stays the recommended choice for
  physics-tight / competitive games.

### Concrete success criteria

1. A demo scene runs in Shared mode with 4 peers, each spawning entities,
   transferring authority on contact, with no dedicated server process.
2. The existing host-mode demo continues to work unchanged.
3. ≥ 50% of `addons/monke-net/src/Shared/` files are bit-identical between
   the two modes (the "Shared" namespace is honest, not aspirational).
4. The mode is selected at runtime by `MonkeNetManager.Initialize(MonkeNetMode)`;
   no separate addons / no separate binaries.

## 2. Topology comparison

```
HOST MODE (current)
                  ┌────────┐
                  │ Server │  (canonical sim, snapshots all entities)
                  └───┬────┘
                ┌─────┼─────┐
                ▼     ▼     ▼
            [Client] [Client] [Client]  (predict+reconcile against server)

SHARED MODE (new)
                  ┌────────┐
                  │ Relay  │  (just forwards, holds no game state)
                  └───┬────┘
                ┌─────┼─────┐
                ▼     ▼     ▼
            [Peer ]  [Peer ]  [Peer ]
              ▲   ◀──peer-to-peer-via-relay──▶  ▲
              └─ each peer owns some entities ──┘
              └─ each peer simulates locally  ──┘
              └─ each peer interpolates remote ─┘
              └─ one peer is "Master Client"  ──┘
```

Key differences from the user's perspective:

| Aspect | Host mode | Shared mode |
|---|---|---|
| Process layout | 1 server + N clients | N peers + 1 relay (or peer-as-relay) |
| Canonical sim | Server only | None — each peer simulates owned entities |
| Snapshot source | Server | Each authority peer broadcasts its owned entities |
| Authority on entity | Always server-state, optional input-authority on client | Per-entity, lives on some peer |
| Cheat resistance | High (server validates) | Low (peers trust each other) |
| Tick rate | 60 Hz physics, 20 Hz snapshot | 60 Hz physics, 20 Hz snapshot (per-peer broadcast) |
| Late join | Server has full state, sends to joiner | Master Client streams aggregated state |

## 3. Module layout in the existing tree

The repo already has a useful three-way split that we should respect:

```
addons/monke-net/src/
  Shared/                    ← mode-agnostic primitives (reusable)
  ServerSide/                ← host-mode authority (stays as-is)
  ClientSide/                ← host-mode consumer (stays as-is)
  SharedSide/  ← NEW         ← shared-mode peer
```

The new directory mirrors Server/Client and contains:

```
SharedSide/
  PeerManager.cs                   (autoload — replaces ServerManager+ClientManager for shared mode)
  InternalComponents/
    PeerEntityManager.cs           (replaces ServerEntityManager+ClientEntityManager)
    PeerStateBroadcaster.cs        (replaces ServerEntityManager.SendSnapshotData)
    PeerStateReceiver.cs           (replaces ClientPredictionManager's snapshot intake)
    MasterClientElector.cs         (NEW)
    LateJoinResponder.cs           (NEW)
    AuthorityTransferRouter.cs     (NEW — peer-to-peer authority RPC routing)
  Nodes/
    SharedNetworkedEntity.cs       (replaces ServerStateSyncronizer/ClientPredictedEntity for shared spawns)
```

`Shared/` keeps everything that's mode-agnostic and gets **lightly extended**
to support both modes; `ServerSide/` and `ClientSide/` stay untouched.

## 4. What is reusable (the honest accounting)

This section is the load-bearing part of the design doc. Adding Shared mode
is only worth doing if the reuse story holds up.

### 4.1 Fully reusable (no/trivial changes)

These files live in `addons/monke-net/src/Shared/` today and work as-is in
both modes:

| File | Why it's mode-agnostic |
|---|---|
| `Shared/PredictionRigidbody3D.cs` | Wraps a `RigidBody3D` with queued forces + snapshot/restore. Doesn't care who is authoritative. |
| `Shared/PredictionVisualSmoothing3D.cs` | Per-body visual smoothing decay. Topology-agnostic — it just absorbs body teleports. |
| `Shared/OfflineRigidbody3D.cs` | Snapshot/restore for non-networked bodies during any kind of resim. |
| `Shared/PhysicsUtils.cs` | `DeltaTime` constant + tick helpers. |
| `Shared/Nodes/NetworkBehaviour.cs` | The `EntityId`/`Authority`/`Metadata` triple is the same in both modes. |
| `Shared/Nodes/Entities/EntitySpawnConfiguration.cs` | Per-type config (Server/Client scene paths, OwnershipPolicy) works the same way — Shared just reads it through a different code path. |
| `Shared/NetworkMessages/EntityEventMessage.cs` | Created/Destroyed event + spawn pose + activation tick. Identical wire format. |
| `Shared/NetworkMessages/GameSnapshotMessage.cs` | The States[] + Inputs[] + InputFrontiers structure works for "snapshot of all entities one peer owns" too. |
| `Shared/NetworkMessages/OwnershipChangeRequestMessage.cs` + `Rejected` + `AuthorityChangedMessage` + `ReleaseAuthorityMessage` | The wire formats are reused; only the routing changes (server→peer-current-owner). |
| `Shared/Entities/EntitySpawner.cs` | The "instantiate scene, set name/EntityId/Authority/Metadata, write spawn pose via `ApplyRigidBodySpawnPose`, optionally activation-delay-freeze" pipeline is mode-agnostic. The `isServerSpawn` parameter becomes `isLocalAuthority`. |
| `Shared/MonkeNetException.cs`, `Shared/MonkeNetComponents.cs`, etc. | Plumbing. |
| `Serializer/*` | Bit-level packing. |

### 4.2 Lightly extended (small additions, behaviour preserved for host)

These need ~5–50 lines of additions but no semantic changes for existing
host-mode callers:

| File | Change |
|---|---|
| `MonkeNetManager.cs` | Add `MonkeNetMode` enum (`HostServer`, `HostClient`, `SharedPeer`); add `Mode` property; route initialisation to the right autoload. Existing initialisation paths default to host. |
| `Shared/Entities/EntitySpawner.cs` | EntityId minting becomes a delegate the manager provides — host uses `++_entityIdCount`; shared uses `(peerId << 24) | localCounter`. Default delegate preserves host behaviour. |
| `Shared/NetworkMessages/*` | Add a new `SharedPeerJoinedMessage` + `MasterClientChangedMessage` + `LateJoinStateMessage`. Existing messages unchanged. |
| `Shared/Nodes/NetworkBehaviour.cs` | `Authority` field already exists. Add a sentinel `Authority.None = -1` for "unowned, master client may despawn." Currently `0` doubles as "server" and "unowned" — needs clearer semantics for Shared mode. |
| `INetworkManager.cs` (ENet wrapper) | Add `SendToPeer(int peerId, ...)` and `SendToAllExcept(int peerId, ...)` if not already present, for relay-style routing. Most likely already there or trivial to add. |

### 4.3 The prediction stack (the most interesting reuse story)

`ClientSide/Nodes/ClientPredictedEntity.cs` and its `PredictionTier` /
`InterpolationPolicy` system **is reusable in Shared mode** but the
semantics of each policy shift:

| Policy | Host mode (today) | Shared mode |
|---|---|---|
| `Hysteresis` | Predict locally, contact upgrades to Resim for 15 ticks, reconcile against server snapshot | Local sim is truth — no upstream to reconcile against. Tier system collapses to "owned ↔ remote-owned" which is just Resim vs Interpolate based on `Authority == localPeerId`. |
| `AlwaysPredict` | Always Resim, reconcile every tick | Always Resim for owned, but no reconcile path. For non-owned, always Resim makes no sense (can't predict what you don't input for). Mostly N/A in Shared. |
| `AuthorityTransfer` | The hybrid that doesn't really work in host mode (see prior conversation) | **THIS IS THE CANONICAL SHARED-MODE PATTERN.** Contact → request authority via peer-to-peer message → current authority approves → flip → local sim is truth. Releases on contact-loss timer. |
| `BlendedVelocity` | Hybrid: kinematic-interp until contact, then Resim with velocity-blend on mispredict | In Shared mode this becomes "while remote-owned, kinematic-interp; on contact-with-locally-owned-player, request authority; while owned, sim locally with velocity-blend correction against incoming remote-authoritative snapshots (e.g. for objects in transition)." |

The `InterpolationPolicy` enum is reused as-is. The `EffectiveTier` switch
already handles the `IsLocallyOwned()` case for `AuthorityTransfer`, which
is exactly the Shared-mode default. The hysteresis counter, contact
detection, RequestResimUpgrade, OnEffectiveTierChanged hooks — all of it
ports cleanly.

`ClientPredictionManager` itself is largely **not** reusable:
- `ProcessServerState` → reused for remote-owned entities (apply incoming
  snapshot from authority peer). Rename to `ProcessRemoteAuthSnapshot`.
- The host-mode reconcile-against-canonical-server loop → not used in
  Shared mode.
- The catch-up resim on first snapshot → reused; needed when receiving a
  late-join state burst from Master Client.
- T2 snapshot interpolation buffer (`_snapshotHistoryByEid`,
  `TryGetInterpolatedSnapshot`, `InterpDelayTicks`) → reused 1:1 for
  Interpolate-tier non-owned entities in Shared mode.
- `RollbackAndResimulate` → not used in Shared mode; there's nothing
  canonical to roll back to.

This suggests the right move is to **extract the snapshot-buffer +
interpolation logic into `Shared/` as a `SnapshotInterpolator` component**
that both `ClientPredictionManager` and the new `PeerStateReceiver` import.

### 4.4 Demo entities

`demo/LocalRigidPropPrediction.cs`, `demo/cube/LocalCube.tscn`,
`demo/ball/LocalBall.tscn` — work as-is in Shared mode for the *consumer*
side (when the local peer doesn't own the entity). On the producer side
(when the local peer owns it), the existing `ResimulateTick` /
`GetSnapshotState` / `ExtractAuthoritative*` overrides are reused to
**broadcast** state instead of **receive** it. The same scene file works
in both modes; only the manager wiring differs.

`demo/players/rigid_player/LocalRigidPlayerPrediction.cs` — same story for
the player. Note: this is the dynamic-rigidbody player we don't want to
touch. In Shared mode it behaves like Fusion 2 Shared with a non-KCC
player — works, but susceptible to cross-peer divergence during dynamic
contact (Fusion 2 acknowledges this limitation). Don't fix it as part of
Shared mode; document it.

`demo/MainScene.tscn` — needs a small companion `SharedMainScene.tscn`
(or a mode toggle) that wires `PeerManager` instead of `ServerManager` +
`ClientManager`. EntitySpawnConfiguration sub-resources are reused.

### 4.5 Test infrastructure

`tests/test-harness/MultiClientHarness.cs` and `tests/Infrastructure/`
work for both. The harness's command dispatcher (`spawn-entity`,
`set-input-schedule`, `sample-state`, `mispredict-count`, etc.) is
behaviour the new `PeerManager` needs to expose for testing — that's a
~50 line shim per command. The orchestrator (`MultiProcessHarness.cs`)
gets a new `Spawn("peer", ...)` role alongside `"server"`/`"client"`.

**Estimate**: ~70% of existing `Shared/` is reused as-is; ~25% gets light
extensions; ~5% (notably the `Authority=0` sentinel cleanup) needs real
semantic work.

## 5. Subsystem designs

### 5.1 Networking layer / relay

**Constraint**: must work with the existing `INetworkManager` (ENet today;
could be swapped). Must NOT require a hosted backend in the open-source
default path.

**Recommended default**: **peer-as-relay**. One peer (the session
creator) opens an ENet server socket and runs a thin relay process that
forwards messages between connected peers. The relay-peer is ALSO a
gameplay peer — it just additionally forwards. From the gameplay
perspective every peer is equal; the relay role is invisible.

This is identical to PUN's model and to Steam Lobbies. Drawbacks: NAT
issues (mitigated by Steamworks or by running the relay on a port-
forwarded peer), single point of failure (mitigated by relay-migration on
disconnect, see §5.7), bandwidth concentrated on relay peer.

**Future**: pluggable relay backends — `IRelay` interface with `EnetRelay`
(default), `SteamRelay`, `EpicRelay`, `DedicatedRelay` (a separate ops-
managed relay that doesn't simulate). Cost is ~1 day to add each backend
once the interface is right.

**Wire format**: existing message envelope is fine. Add two header bits:
- `Target`: 0 = broadcast (relay fans out), 1 = unicast (relay forwards
  to specific peer)
- `Originator`: peer id of the sender (relay doesn't trust client claim
  — it overwrites with the source connection's id)

### 5.2 PeerManager (replaces ServerManager + ClientManager for shared mode)

```csharp
// addons/monke-net/src/SharedSide/PeerManager.cs
public partial class PeerManager : Node
{
    public static PeerManager Instance { get; private set; }

    // Local peer id. -1 until joined.
    public int LocalPeerId { get; private set; } = -1;

    // List of all peers in the room. Updated by the relay on join/leave.
    public IReadOnlyList<int> Peers => _peers;

    // The Master Client (one of the peers). Used for scene-baked object
    // authority, force-despawn, late-join arbitration.
    public int MasterClient { get; private set; } = -1;

    [Signal] public delegate void PeerJoinedEventHandler(int peerId);
    [Signal] public delegate void PeerLeftEventHandler(int peerId);
    [Signal] public delegate void MasterClientChangedEventHandler(int oldMaster, int newMaster);

    public override void _PhysicsProcess(double delta)
    {
        // 1. Apply received input for any entity we don't own (or skip — Shared has no input forwarding)
        // 2. Run OnProcessTick on every locally-owned entity
        // 3. SpaceStep
        // 4. OnPostPhysicsTick for owned entities
        // 5. RegisterState for snapshot history
        // 6. Every 3 ticks (20 Hz @ 60 Hz physics): _broadcaster.BroadcastOwnedState()
    }
}
```

This is intentionally shaped like a merged `ServerManager` + `ClientManager`.
The mode picker in `MonkeNetManager.Initialize` decides which of the three
autoloads to enable.

### 5.3 EntityId namespacing

Current: `_entityIdCount = 0; ... entityId = ++_entityIdCount;` (host
mints all ids).

Shared: each peer mints ids in its own namespace, collisions impossible.

```csharp
// 64-bit struct, or pack into int as below
public readonly struct EntityId : IEquatable<EntityId>
{
    public int PeerId { get; }       // top 8 bits — 256 max peers
    public int LocalCounter { get; } // bottom 24 bits — 16M ids per peer

    public int Packed => (PeerId << 24) | (LocalCounter & 0xFFFFFF);

    public EntityId(int peerId, int counter) { PeerId = peerId; LocalCounter = counter; }
    public static EntityId FromPacked(int packed) => new(packed >> 24, packed & 0xFFFFFF);
}
```

Migration plan: keep `int EntityId` as the public type (24 ms of compat
break is bad). Encode (peerId, counter) into the int. Host mode keeps
peerId = 0 always so all existing ids stay valid (`MyHostId = 0` reads
the same as today).

### 5.4 State replication

**Send side** (`PeerStateBroadcaster`):
- Every 3 physics ticks (= 20 Hz at 60 Hz physics, matches Fusion 2),
  iterate locally-owned entities, call `PackEntityState` on each, wrap
  into a `GameSnapshotMessage`, broadcast via relay.
- This is structurally identical to `ServerEntityManager.SendSnapshotData`,
  just iterating "owned by me" instead of "all entities."

**Receive side** (`PeerStateReceiver`):
- For each entity in the incoming snapshot:
  - If `entity.Authority == LocalPeerId`: ignore (this is a stale echo;
    shouldn't happen if the relay is honest, but defensively drop).
  - If entity is `EffectiveTier == Interpolate`: feed into the snapshot
    history buffer for kinematic interp. This is the existing T2 logic
    extracted into `Shared/SnapshotInterpolator`.
  - If entity is `EffectiveTier == Resim` (owned via authority transfer
    that hasn't completed?): this is a transition window edge case; apply
    `BlendedVelocity`-style soft correction.

**Snapshot history buffer**: reuse the existing T2 implementation
(`_snapshotHistoryByEid` + `TryGetInterpolatedSnapshot`) — it doesn't care
who's sending the snapshots, just stores them by tick.

### 5.5 Authority transfer (peer-to-peer)

Current: client → `OwnershipChangeRequestMessage` → server → server
evaluates `OwnershipPolicy` → server broadcasts `AuthorityChangedMessage`.

Shared: peer X → `OwnershipChangeRequestMessage` → relay → current
authority peer (peer Y) → peer Y evaluates `OwnershipPolicy` AND its own
`AllowStateAuthorityOverride` flag → peer Y broadcasts
`AuthorityChangedMessage` → relay fans out → every peer updates
`entity.Authority`.

```csharp
// AuthorityTransferRouter.cs
public void OnReceived(OwnershipChangeRequestMessage msg, int fromPeerId)
{
    var entity = _spawner.FindById(msg.EntityId);
    if (entity == null) return;
    if (entity.Authority != LocalPeerId) {
        // I'm not the current authority — forward to whoever is.
        // (Or drop, depending on whether we trust the requesting peer.
        // Forwarding is the simpler model.)
        _relay.SendToPeer(entity.Authority, msg);
        return;
    }
    if (!ApprovePolicy(entity, fromPeerId)) {
        _relay.SendToPeer(fromPeerId, new OwnershipChangeRejectedMessage(msg.EntityId));
        return;
    }
    // Approve. Broadcast new authority.
    entity.Authority = fromPeerId;
    _relay.Broadcast(new AuthorityChangedMessage { EntityId = msg.EntityId, NewAuthority = fromPeerId });
}
```

`OwnershipPolicy` is reused as-is. `AllowStateAuthorityOverride` is added
as a new field on `EntitySpawnConfiguration` (default false — matches
Fusion 2's documented behaviour).

### 5.6 Master Client

Election rule: **lowest peer id among connected peers**. Deterministic,
no protocol needed — every peer computes it independently from
`Peers.Min()`. When a peer joins or leaves, `MasterClient` is recomputed.

Master Client responsibilities:
- Owns scene-baked entities (entities not spawned by any peer at runtime).
- Owns entities whose authority became `None` (owner disconnected).
- Resolves late-join state requests (§5.7).
- Can force-despawn any entity (admin power for failure recovery).

Migration: on `PeerLeft(masterPeerId)`, every peer recomputes
`MasterClient = Peers.Min()` and emits `MasterClientChanged`. Entities
the old Master owned are reassigned to the new Master.

### 5.7 Late join

Joining peer connects → relay forwards a `PeerJoinedMessage` to all
existing peers → Master Client responds with a `LateJoinStateMessage`
containing:
- All entities currently networked (from Master's view), each with
  current pose/velocity/authority/metadata.
- Current Master Client id.
- Current room tick (for clock sync).

Joiner spawns each entity locally, applies state, jumps clock to room
tick. Any snapshots that arrive during the join are buffered and applied
post-spawn.

If the Master Client disconnects mid-join, the relay re-routes the
request to the new Master (recomputed on the relay).

**Data volume**: a 50-entity scene at ~64 bytes/entity = 3.2 KB. Fits in
a single packet. Larger scenes need streaming + ack — Phase 2 work.

### 5.8 Relay migration on disconnect

If the relay-peer disconnects (because peer-as-relay = relay is also a
gameplay peer):
- Remaining peers detect by relay socket close.
- Election: lowest remaining peer id opens a new relay socket.
- All other peers reconnect to the new relay.
- Authority of objects the disconnected peer owned: `None`, Master picks up.

Phase 1 can leave this rough (just disconnect everyone if relay dies) and
ship migration in Phase 2. Most VR/MR/casual lobbies tolerate a
"relay-host quit, lobby ended" UX.

## 6. Physics in Shared mode

Mostly already worked out via T2's tier system:

- **Locally-owned entities**: `EffectiveTier = Resim`, `Freeze = false`,
  dynamic body, integrated by local `SpaceStep`. The local sim IS the
  truth.
- **Remotely-owned entities**: `EffectiveTier = Interpolate`,
  `Freeze = true` (kinematic), driven by snapshot interpolation buffer
  with `InterpDelayTicks` of buffer. T2's `OnPostPhysicsTick` kinematic-
  interp code reused 1:1.
- **In-transition** (authority requested but not yet granted): treat as
  remote-owned until `AuthorityChangedMessage` arrives. Don't predict
  optimistically — matches Fusion 2's documented behaviour.
- **Forecast extrapolation** (Fusion 2.1 feature): future work.

Cross-peer contact between two locally-owned bodies happens entirely
locally and is fine. Contact between a locally-owned and remotely-owned
body has the same "kinematic wall feel" trade-off T2 already documents —
either the remote body upgrades to dynamic via authority transfer, or it
acts as an immovable obstacle until the next snapshot updates its pose.

No new physics work needed beyond what T2 already does.

## 7. Coexistence with host mode in the same codebase

```
MonkeNetManager.Initialize(MonkeNetMode mode, ...)
   │
   ├─ HostServer  →  ServerManager  +  ServerEntityManager  +  ServerInputReceiver
   │
   ├─ HostClient  →  ClientManager  +  ClientEntityManager  +  ClientPredictionManager
   │                                                          (T2 snapshot buffer)
   │
   └─ SharedPeer  →  PeerManager  +  PeerEntityManager  +  PeerStateBroadcaster
                                                          +  PeerStateReceiver
                                                          (T2 snapshot buffer — shared via Shared/SnapshotInterpolator)
                                                          +  MasterClientElector
                                                          +  LateJoinResponder
                                                          +  AuthorityTransferRouter
```

Mode picker at scene-load time. A project can ship a single demo scene
with a "Mode" dropdown in the lobby UI. The library exposes only the
nodes for the active mode (e.g. `PeerManager.Instance` is null in host
mode and vice versa).

Each entity scene file declares which `EntitySpawnConfiguration` it
uses; the config has `ServerScene` (host mode) and `ClientScene` (host
mode) paths. Add `SharedScene` for the shared-mode variant. In most
cases SharedScene == ClientScene because the consumer-side scene
already has prediction wiring; the producer-side broadcast can run on
the same scene.

### Mode-agnostic API surface

```csharp
MonkeNetManager.Mode                                  // HostServer | HostClient | SharedPeer
MonkeNetManager.IsAuthorityForEntity(entityId)        // works in both modes
MonkeNetManager.LocalEntityId                         // local peer id (HostServer mode = 0)
MonkeNetManager.AllPeerIds                            // host mode: [server, ...connectedClients]; shared: [...allPeers]
MonkeNetManager.IsMasterClient                        // host mode: true on server; shared: peer == lowest-id
```

Game code that uses these stays mode-agnostic. Game code that uses
`ServerManager.Instance` directly is host-only by construction.

## 8. Testing

Add a new `MultiProcess` test orchestration role: `peer`. The orchestrator
spawns N homogeneous peers + designates one to also run the relay. Most
existing test scenarios can be ported with mechanical renames:

- `MultiProcessOffsetPushTests`: replace `(server, client)` with
  `(peer1, peer2)` where peer1 spawns the cube + player owned by peer1;
  peer2 observes; assert peer2's visual matches peer1's body within
  budget.
- `MultiProcessSleepCoherenceTests`: same scenario but cubes are spawned
  by peer1 with `AuthorityTransfer` so authority can flip on contact.
- `MultiProcessMispredictTests` policy variants: each policy still works,
  but the budgets differ for Shared mode. New `SharedHysteresis`,
  `SharedAuthorityTransfer` variants.

**New test suite**: `MultiProcessSharedModeTests`
- Master Client election on join order
- Master Client migration on disconnect
- Authority transfer via peer-to-peer (not server arbitration)
- Late-join state burst from Master
- 4-peer drop-in/drop-out scenario
- Relay-peer disconnect and lobby teardown

Estimate: ~1 week test infrastructure + ~1 week new test scenarios.

## 9. Cheating surface and optional mitigations

Default Shared mode trusts peers. Documented prominently in README + the
Shared mode quick-start. Optional mitigations the library can ship as
opt-in:

1. **Per-property validators**: `[NetworkedRange(0, 100)]` attribute (or
   a `Validate(receivedState)` virtual on the entity) that rejects out-
   of-band values from a remote authority. Validation runs on receivers.
2. **Master-client validates spawn**: spawn requests from non-Master
   peers are forwarded to Master, who approves/rejects via
   `OwnershipPolicy` before broadcast.
3. **Hybrid mode**: ship a "dedicated authority peer" that runs in
   Shared topology but never claims authority on gameplay entities. It
   acts as a "watcher" that can kick peers exhibiting blatant cheating
   (out-of-band positions, impossible velocity deltas). This requires
   hosting but is much lighter than a full host-mode server.

All of these are Phase 2.

## 10. What's deliberately out of scope (Phase 2+)

- Forecast Physics (Fusion 2.1's extrapolation system for remote bodies).
- Late-join streaming for very-large scenes (> 1 packet of state).
- Steam / Epic relay backends.
- Per-region relay selection (Photon Cloud-style geo-routing).
- Persistent rooms ("session that outlives all peers").
- Spectator role (peer that receives state but never claims authority).
- Cheat-detection / replay validation (Phase 2 mitigations above).
- KCC-style deterministic player controller. The dynamic-rigidbody
  player is documented as the Shared-mode trade-off; replacing it is a
  separate workstream.

## 11. Scope summary

| Phase | Deliverable | Estimate |
|---|---|---|
| Phase 0 | Design doc (this file) | done |
| Phase 1a | Networking layer + relay | 1.5 weeks |
| Phase 1b | `PeerManager` + `PeerEntityManager` skeleton | 2 weeks |
| Phase 1c | State replication (broadcast + receive) | 1 week |
| Phase 1d | EntityId namespacing | 1 week |
| Phase 1e | Authority transfer (peer-to-peer routing) | 0.5 week |
| Phase 1f | Master Client election | 0.5 week |
| Phase 1g | Late-join burst | 1 week |
| Phase 1h | Mode picker + coexistence wiring | 0.5 week |
| Phase 1i | Test infrastructure + Shared-mode tests | 2 weeks |
| **Phase 1 total** | Shippable Shared mode | **~10 weeks** |
| Phase 2 | Mitigations, Forecast Physics, alt relay backends | TBD |

Of which: roughly **40% of Phase 1 hours are reused logic** (snapshot
buffer extraction, message format reuse, EntitySpawner refactor that's
incidentally also useful for host mode). The actual "new code" is about
6 weeks of original work.

## 12. Open questions / things to decide before starting

1. **EntityId encoding**: 24-bit local counter is 16M ids per peer.
   Probably enough. Alternative: 64-bit struct, breaks ABI more. Cost
   of decision: low — picking 24-bit + reserving a future
   `EntityIdWide` extension is fine.
2. **Default `AllowStateAuthorityOverride`**: Fusion 2 docs imply false.
   Recommend false. Means most objects need explicit
   `ReleaseStateAuthority` before another peer can claim — safer
   default.
3. **Authority sentinel `0` cleanup**: today `0` = "server" in host
   mode AND "unowned" semantically. In Shared mode these collide.
   Suggest: introduce `Authority.None = -1` for "unowned"; `0` stays
   as "host-mode server" for backward compat. Document explicitly.
4. **Master Client election rule**: lowest peer id is simple, doesn't
   need a protocol, but means the original Master is replaced by the
   second-joiner on disconnect (not the longest-connected peer). Is
   that ok for game design? Fusion 2's rule is undocumented in the
   pages we have; lowest-id is what PUN does and seems fine.
5. **Snapshot rate**: Fusion 2 Shared = 20 Hz fixed. monke-net host
   uses 60 Hz physics + a configurable snapshot interval. Suggest:
   match Fusion 2 with 20 Hz Shared default, but keep it configurable.
   Per-tick state writes (60 Hz) burn way too much bandwidth in Shared
   mode for typical Photon Cloud plans.
6. **Relay backend abstraction**: ship the `IRelay` interface even if
   only ENet implements it in Phase 1. Sets up future Steam/Epic
   backends without ABI breaks.

---

## Appendix A: file-by-file reuse table

For someone implementing this, here's the granular reuse story. Files in
`Shared/` not listed below are unchanged.

| Current file | Shared-mode use | Refactor needed |
|---|---|---|
| `Shared/PredictionRigidbody3D.cs` | as-is | none |
| `Shared/PredictionVisualSmoothing3D.cs` | as-is | none |
| `Shared/OfflineRigidbody3D.cs` | as-is (used during local resim) | none |
| `Shared/PhysicsUtils.cs` | as-is | none |
| `Shared/Entities/EntitySpawner.cs` | reused | `isServerSpawn` → `isLocalAuthority`; EntityId minting delegated |
| `Shared/Nodes/NetworkBehaviour.cs` | reused | add `Authority.None = -1` sentinel |
| `Shared/Nodes/Entities/EntitySpawnConfiguration.cs` | reused | add `SharedScene`, `AllowStateAuthorityOverride` |
| `Shared/Nodes/Entities/OwnershipPolicy.cs` | as-is | none |
| `Shared/NetworkMessages/EntityEventMessage.cs` | as-is | none |
| `Shared/NetworkMessages/GameSnapshotMessage.cs` | as-is | none |
| `Shared/NetworkMessages/AuthorityChangedMessage.cs` | as-is | none |
| `Shared/NetworkMessages/OwnershipChange*.cs` | as-is | none — only routing changes |
| `Shared/NetworkMessages/ReleaseAuthorityMessage.cs` | as-is | none |
| **NEW** `Shared/NetworkMessages/PeerJoinedMessage.cs` | net-new | — |
| **NEW** `Shared/NetworkMessages/MasterClientChangedMessage.cs` | net-new | — |
| **NEW** `Shared/NetworkMessages/LateJoinStateMessage.cs` | net-new | — |
| **NEW** `Shared/SnapshotInterpolator.cs` | extracted from `ClientPredictionManager` | extract `_snapshotHistoryByEid` + `TryGetInterpolatedSnapshot` |
| **NEW** `Shared/EntityId.cs` | net-new | (24 + 8 bit packed) |
| `ClientSide/ClientManager.cs` | host-only | none |
| `ClientSide/InternalComponents/ClientPredictionManager.cs` | host-only | extract snapshot buffer; leave reconcile/rollback as host-only |
| `ClientSide/Nodes/ClientPredictedEntity.cs` | reused | policy semantics shift; `EffectiveTier` switch already supports this |
| `ServerSide/*` | host-only | none |
| `MonkeNetManager.cs` | both | add `Mode` enum + dispatch |
| `INetworkManager.cs` | both | add unicast/broadcast helpers if not present |
| **NEW** `SharedSide/PeerManager.cs` | shared-only | — |
| **NEW** `SharedSide/InternalComponents/PeerEntityManager.cs` | shared-only | — |
| **NEW** `SharedSide/InternalComponents/PeerStateBroadcaster.cs` | shared-only | — |
| **NEW** `SharedSide/InternalComponents/PeerStateReceiver.cs` | shared-only | — |
| **NEW** `SharedSide/InternalComponents/MasterClientElector.cs` | shared-only | — |
| **NEW** `SharedSide/InternalComponents/LateJoinResponder.cs` | shared-only | — |
| **NEW** `SharedSide/InternalComponents/AuthorityTransferRouter.cs` | shared-only | — |
| **NEW** `SharedSide/Nodes/SharedNetworkedEntity.cs` | shared-only | — |

## Appendix B: References

- Fusion 2 Network Topologies — https://doc.photonengine.com/fusion/current/manual/network-topologies
- Fusion 2 Network Object (authority API) — https://doc.photonengine.com/fusion/current/manual/network-object
- Fusion 2 Shared Mode Master Client — https://doc.photonengine.com/fusion/current/manual/shared-mode-master-client
- Fusion 2 Physics (Forecast Physics) — https://doc.photonengine.com/fusion/current/manual/physics
- Fusion 2 Choose a Topology — https://doc.photonengine.com/fusion/current/fusion-choose
- Fusion 2 Release Notes (Shared-mode tick alignment, 20 Hz, etc.) — https://doc.photonengine.com/fusion/current/getting-started/release-notes/release-notes-2-0
- Prior in-repo design conversations: T2 kinematic-interp (extracted snapshot buffer), AuthorityTransfer policy investigation (this is what motivated the Shared-mode investigation).
