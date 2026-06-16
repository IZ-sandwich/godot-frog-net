# 🐸 Frog-Net (Monke-Net with physics)

This repository is a fork of [Monke-net](https://github.com/grazianobolla/godot-monke-net) that has made many modifications to the library to achieve networking with smooth physics.

This library is a C# Godot Addon that facilitates creating robust multiplayer games using the Client-Authoritative Server architecture, including client-side prediction, entity interpolation, physics re-simulation, lag compensation and more! The full set of features provided by this library is documented in [FEATURES.md](FEATURES.md).

For a code-level tour of the addon's internals — manager classes, base classes to extend, and a step-by-step "adding a new entity type" walkthrough — see [README-quickstart.md](README-quickstart.md).

---

> [!WARNING]
> VERY IMPORTANT! Right now FrogNet, just like MonkeNet, uses its [own fork of Godot!!!](https://github.com/grazianobolla/godot/tree/daylily-zeleen/physics_space_step) (This link is for 4.7.0) for networked physics!!! Due to current limitations on Godot Physics and the Jolt module it is impossible to step the physics world manually in vanilla Godot.

## 🧩 Dependencies and .NET environment
MonkeNet requires your game project to use .NET 8, also it heavily uses [ImGui](https://github.com/ocornut/imgui) to display very important debug information, this means you will also have to install [ImGui Godot](https://github.com/pkdawson/imgui-godot) in your project, that's MonkeNet only dependency.

## 💾 Installation
FrogNet is a Godot addon, to start using it copy the `addons\monke-net\` folder, paste it into your project `addons\` folder, and enable the plugin in your project settings. After this, you will have access to all FrogNet resources.

## 📦 This Repository (Demo Project)

This repository ships the addon together with a small playable demo so you can see every networking feature exercised end-to-end. Open the project in Godot and run `demo/MainScene.tscn` — the menu lets you Host, Connect, or Host & Play (listen-server) on `localhost:9999`.

Once connected, the in-game UI lets you spawn one of every entity type the demo supports:

| Entity | Folder | What it demonstrates |
|---|---|---|
| **Rigid-body player** | [demo/players/rigid_player/](demo/players/rigid_player/) | Locally-predicted `RigidBody3D` player with full rollback + resim, contact-driven Interpolate→Resim upgrade on touched props, first-person camera, animation driver. |
| **Ball** | [demo/ball/](demo/ball/) | Server-authoritative bouncy prop. Every client predicts it locally and reconciles against the snapshot — uses the shared `LocalRigidPropPrediction` script. |
| **Cube** | [demo/cube/](demo/cube/) | Server-authoritative box prop. Same prediction path as the ball; used in the stack-push test scenarios. |
| **Vehicle** | [demo/vehicle/](demo/vehicle/) | Drivable vehicle that walks the **authority-transfer** flow: an unowned vehicle is claimable via proximity prompt (press **F**), gated by an `OwnershipPolicy` resource and a custom server-side approver. The "Claim Vehicle" button bypasses proximity for testing. |
| **Map** | [demo/map/](demo/map/) | Static environment, lighting, obstacles. |

Supporting demo files:

- [demo/MainScene.cs](demo/MainScene.cs) — wires Host / Connect / Host-and-Play buttons to `MonkeNetManager`, hooks the server-side vehicle approver, handles connection / silent-server / reconnect UI.
- [demo/MainScene.tscn](demo/MainScene.tscn) — the demo's main scene. Holds the `MonkeNetConfig` node where every `EntitySpawnConfiguration` is registered.
- [demo/NetworkMessages.cs](demo/NetworkMessages.cs) — game-specific input and state structs (`IPackableElement` / `IEntityStateData`). Auto-discovered by `MessageSerializer` at startup.
- [demo/LocalRigidPropPrediction.cs](demo/LocalRigidPropPrediction.cs) — generic client-side prediction wrapper shared by the ball, cube, and dummy vehicle. Reference implementation for any server-authoritative rigid prop.

## 📐 Project Structure

```
addons/monke-net/      ← the library (treat as read-only when consuming)
│  ├─ src/Shared/      ← managers, network layer, serializer, prediction primitives
│  ├─ src/ServerSide/  ← ServerManager, entity manager, input receiver, lag compensation
│  ├─ src/ClientSide/  ← ClientManager, prediction manager, snapshot interpolator
│  ├─ scenes/          ← MonkeNet.tscn, ServerManager.tscn, ClientManager.tscn
│  └─ resources/       ← editor icons used on [GlobalClass] nodes
│
demo/                  ← playable demo project (see table above)
│  ├─ MainScene.tscn
│  ├─ players/         ← rigid-body player + shared movement / camera / input producer
│  ├─ ball/, cube/, vehicle/, map/
│  └─ NetworkMessages.cs, LocalRigidPropPrediction.cs
│
tests/                 ← gdUnit4 test suites
│  ├─ Unit/            ← pure-C# unit tests
│  ├─ Integration/     ← in-process integration tests against the live tick loop
│  ├─ Quantitative/    ← metric-producing scenarios (smoothing, push-back, stack-push)
│  ├─ MultiProcess/    ← two-process scenarios driven over TCP orchestration
│  ├─ StackPush/       ← focused stack-push regression cases
│  └─ Infrastructure/  ← shared metrics, CSV emitters, harness wiring
│
test-harness/          ← MultiClientHarness — TCP-driven test client embedded in
│                       MainScene when launched with `-- --test-harness`
│
tools/                 ← Python plot/analysis scripts + PowerShell test helpers
│                       for the artefacts produced by the Quantitative suites
│
README.md              ← this file
README-quickstart.md   ← code-level tour of the addon
FEATURES.md            ← full feature list
project.godot, monke-net.sln, monke-net.csproj
```

### Suggested reading order

1. **[FEATURES.md](FEATURES.md)** — what the library can do.
2. **[README-quickstart.md](README-quickstart.md)** — how the pieces fit together; where to put your code.
3. **`demo/`** — a working reference for every feature in the list. Copy from here when building a new entity type.

---
