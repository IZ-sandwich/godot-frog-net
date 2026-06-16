# Frog-Net Quickstart

Server-authoritative multiplayer addon for Godot 4 / C#. The server runs Jolt physics and holds authoritative state. **Every client predicts every entity locally** and reconciles against per-tick server snapshots; "authority" only governs whose input drives an entity, not who runs the simulation.

For the user-facing feature list, see [FEATURES.md](FEATURES.md).

---

## Repository Layout

```
addons/monke-net/   ← the addon (treat as read-only when consuming)
demo/               ← working example to copy from
```

---

## Core Concepts

### Two scenes per entity type

Every networked entity needs two Godot scenes, registered as an `EntitySpawnConfiguration` on the `MonkeNetConfig` node in your main scene (`demo/MainScene.tscn` → `MonkeNet/MonkeNetConfig`):

| Scene | Spawned on | Purpose |
|---|---|---|
| **ServerScene** | Server (+ listen-server) | Runs authoritative physics, receives input, produces state snapshots. |
| **ClientScene** | **Every** connected client | Predicts the entity locally (Jolt sim + snapshot reconcile + visual smoothing). Authority field decides whose input drives it; non-authority clients resolve input from the per-snapshot input cache. |

The unified-prediction refactor collapsed the old `ClientAuthorityScene` / `ClientDummyScene` pair into the single `ClientScene`. There is no separate "interpolated dummy" base class anymore — entities that don't need full rollback opt into `PredictionTier.Interpolate` and the framework absorbs drift by blending instead.

### Physics tick loop

The addon disables Godot's automatic physics and steps the world manually once per network tick via the modified Godot build's `PhysicsServer3D.SpaceStep`. **Do not use `_PhysicsProcess` for gameplay logic** — use the tick callbacks below. Jolt Physics is required.

### Prediction tier (per entity type)

`ClientPredictedEntity` exposes `BaseTier` (`Resim` / `Interpolate`) and `Policy` (`Hysteresis` / `AlwaysPredict` / `KinematicInterpolation`) in the inspector:

- **Resim** (default) — full-scene rollback + resimulate on misprediction. Use for players, vehicles, and props that must feel maximally responsive.
- **Interpolate** + KinematicInterpolation *(default for Interpolate-tier props)* — body is frozen kinematic and pose-driven from snapshot history (Photon Fusion 2 pattern); unfreezes and runs Resim for the hysteresis window on contact with a Resim entity, then re-freezes.
- **Interpolate** + Hysteresis — body runs local Jolt sim steady-state; sub-threshold drift is absorbed by a per-entity blend; on contact with a Resim entity the prop temporarily upgrades to Resim (default 15-tick window) so player–prop interactions stay crisp.

See [FEATURES.md](FEATURES.md#two-tier-client-side-prediction) for the trade-offs.

---

## Key Files

### Addon internals (`addons/monke-net/src/`)

| File | What it does |
|---|---|
| `Shared/MonkeNetManager/MonkeNetManager.cs` | Singleton. Call `CreateServer`, `CreateClient`, or `CreateListenServer` here. Disables Godot's auto-physics in `_EnterTree`. |
| `Shared/Nodes/MonkeNetConfig.cs` | Config node in your scene. Holds the array of `EntitySpawnConfiguration`s and the currently active `InputProducerComponent`. |
| `Shared/Nodes/Entities/EntitySpawnConfiguration.cs` | `Resource` holding the `EntityType` byte, `ServerScene`, `ClientScene`, and optional `OwnershipPolicy`. |
| `Shared/Nodes/Entities/OwnershipPolicy.cs` | Per-entity-type policy for authority-claim approval (proximity, require-unowned, allow-release). Null = reject all. |
| `Shared/Entities/EntitySpawner.cs` | Instantiates entity scenes, wires `NetworkBehaviour`, separates collision layers in listen-server mode. |
| `Shared/PredictionRigidbody3D.cs` | Wrapper around `RigidBody3D` — forces, impulses, and velocity writes are queued and flushed deterministically once per tick. Required for any rigid-body networked entity. |
| `Shared/PredictionVisualSmoothing3D.cs` | Owns the visual node's render-frame transform; absorbs body teleports from reconcile/rollback into a decaying offset. |
| `Shared/MessageSerializer/MessageSerializer.cs` | Auto-discovers every `IPackableElement` / `IEntityStateData` at startup; binary serialization at runtime. |
| `ServerSide/ServerManager.cs` | Drives the server tick loop, calls `OnProcessTick` on server entities, steps physics, broadcasts snapshots. |
| `ServerSide/InternalComponents/ServerEntityManager.cs` | Spawns/destroys entities server-side and syncs world state to connecting clients. Access via `ServerManager.Instance.SpawnEntity<T>()`. Exposes `OwnershipApprover` escape hatch. |
| `ServerSide/InternalComponents/ServerInputReceiver.cs` | Buffers per-entity inputs from clients; called each tick by `ServerManager`. |
| `ServerSide/InternalComponents/LagCompensation.cs` | Per-tick rewind for raycasts; see `LagCompensation.Instance.RaycastAtTick`. |
| `ClientSide/ClientManager.cs` | Drives the client tick loop. Skips `SpaceStep` in listen-server mode (the server side already stepped the shared Jolt space). |
| `ClientSide/InternalComponents/ClientInputManager.cs` | Calls `InputProducer.GenerateCurrentInput()` each tick, packs and sends inputs. |
| `ClientSide/InternalComponents/ClientPredictionManager.cs` | Calls `OnProcessTick` on every predicted entity, detects mispredictions, runs rollback + resim for Resim entities, dispatches blend reconcile for Interpolate entities. |
| `ClientSide/InternalComponents/ClientEntityManager.cs` | Tracks client-side scene instances, manages reconnect / persistent-identity binding. |

### Node base classes to extend

| Base class | Extend for |
|---|---|
| `ServerStateSyncronizer` | The `ServerScene`'s root logic node — implement `OnProcessTick(tick, input)` (apply input, drive body) and `PackEntityState()` (return current state). |
| `ClientPredictedEntity` | The `ClientScene`'s root logic node — implement `OnProcessTick`, `HasMisspredicted`, `HandleReconciliation`, `ResimulateTick`, plus the state-extractor overrides (`GetSnapshotState`, `ExtractAuthoritativePosition/Velocity/Rotation`). For Interpolate-tier props, also override `HandleInterpolateReconciliation` for the blend path. |
| `InputProducerComponent` | A per-local-entity input reader — implement `GenerateCurrentInput()`. Sets `Current = true` in `_Ready` to register with `MonkeNetConfig`. |

### Demo files to copy from (`demo/`)

| File | Start here when making |
|---|---|
| [demo/players/rigid_player/ServerRigidPlayerStateSyncronizer.cs](demo/players/rigid_player/ServerRigidPlayerStateSyncronizer.cs) | Any server entity that uses a `RigidBody3D` |
| [demo/players/rigid_player/LocalRigidPlayerPrediction.cs](demo/players/rigid_player/LocalRigidPlayerPrediction.cs) | Any locally-driven predicted entity (the player or a vehicle) |
| [demo/LocalRigidPropPrediction.cs](demo/LocalRigidPropPrediction.cs) | Any server-authoritative rigid prop — generic reconcile + visual-smoothing path shared by the ball, cube, and dummy vehicle |
| [demo/players/local_player/PlayerInputProducer.cs](demo/players/local_player/PlayerInputProducer.cs) | Any input producer |
| [demo/players/local_player/FirstPersonCameraController.cs](demo/players/local_player/FirstPersonCameraController.cs) | First-person camera tied to a predicted entity |
| [demo/NetworkMessages.cs](demo/NetworkMessages.cs) | Game-specific input / state / command message structs |
| [demo/MainScene.cs](demo/MainScene.cs) | How to start server / client / listen-server and wire the ownership approver |

---

## Network Messages

Two interfaces drive serialization (binary, no reflection at runtime):

- **`IPackableElement`** — input messages and entity state. Implement `WriteBytes` / `ReadBytes` / `GetCopy`.
- **`IEntityStateData`** — same shape but specifically for per-entity state inside snapshots (also carries `EntityId`).

Every struct that implements either is auto-discovered by `MessageSerializer` at startup via reflection. Define them in your own namespace (see [demo/NetworkMessages.cs](demo/NetworkMessages.cs)).

---

## Adding a New Entity Type

1. **Define messages** in your `NetworkMessages.cs`:
   - An input struct (`IPackableElement`) if the entity accepts input.
   - A state struct (`IEntityStateData`) for snapshot data.

2. **Create two scenes** (copy from `demo/`):
   - `ServerFoo.tscn` — root `RigidBody3D` (or `CharacterBody3D`), with `PredictionRigidbody3D` and a `ServerStateSyncronizer` subclass as children. For listen-server compatibility, set `collision_layer = 32768, collision_mask = 32769`.
   - `LocalFoo.tscn` (the unified `ClientScene`) — same body topology plus `PredictionVisualSmoothing3D` wired to a separate `VisualRoot`. Root extends `ClientPredictedEntity`. Set `collision_layer = 2, collision_mask = 3`.

3. **Register** a new `EntitySpawnConfiguration` resource on the `MonkeNetConfig` node with an `EntityType` byte, the two scenes, and (optionally) an `OwnershipPolicy` if clients are allowed to claim authority.

4. **Spawn** from server code:
   ```csharp
   ServerManager.Instance.SpawnEntity<Node3D>(entityType: 5, authority: 0);
   // authority = clientId for client-driven, 0 for server-owned (every client just predicts it)
   ```

5. **Request** from client code (client asks server to spawn for them, server assigns authority to the requester):
   ```csharp
   ClientManager.Instance.MakeEntityRequest(entityTypeByte);
   ```

For a server-authoritative prop where no client owns input (ball / cube), have the client send a custom command message instead and call `SpawnEntity` with `authority: 0` server-side — see `OnServerCommandReceived` in [demo/MainScene.cs](demo/MainScene.cs).

---

## Listen Server

Call `MonkeNetManager.Instance.CreateListenServer(port)` instead of separate `CreateServer` / `CreateClient`. Internally this runs both managers in the same process with separate `SceneMultiplayer` instances so their ENet peers don't clobber each other. `ClientManager` also skips its own `SpaceStep` in this mode — the server side already advanced the shared Jolt space.

**Collision layers are required** when running listen-server: the server's hidden bodies and the local client's visible bodies share the same Jolt space. The addon overrides collision layers automatically at spawn time (`EntitySpawner`), but bake the correct values into your `.tscn` files as well, because Jolt assigns a body's broad-phase layer at `AddChild` time from the initial node values:

| Layer | Value | Who |
|---|---|---|
| Environment | `1` | Static geometry (default) |
| Client | `2` | Every entity instantiated from `ClientScene` (the visible bodies on every client) |
| Server | `32768` (layer 16) | Every entity instantiated from `ServerScene` (hidden authoritative bodies on the listen-server) |

**Mask values:**
- Server entities (`ServerScene`): `mask = 32769` (layer 1 + layer 16) — collide with environment AND with each other so the server's authoritative simulation can resolve player–prop interactions.
- Client entities (`ClientScene`): `mask = 3` (layer 1 + layer 2) — collide with environment AND with each other so client prediction matches.

A server entity with `mask = 1` only sees the environment and silently passes through every other server entity — this looks like "the player can't push the ball". Don't use that pattern unless you specifically want the entity to be untouchable by other server entities.

---

## Shared Physics Utility

`PhysicsUtils.MoveAndSlide(CharacterBody3D)` — call this from a server tick handler if you have a `CharacterBody3D`-backed entity instead of `RigidBody3D`. It normalizes delta time to the fixed network tick rate so simulation stays consistent regardless of frame rate. The current demo uses `RigidBody3D` for every entity, so this utility is provided for game code that prefers character-controller physics.
