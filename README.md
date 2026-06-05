# 🐸 Frog-Net (Monke-Net with physics)

This repository is a fork of [Monke-net](https://github.com/grazianobolla/godot-monke-net) that has made many modifications to the library to achieve networking with smooth physics.

C# Godot Addon that facilitates creating robust multiplayer games using the Client-Authoritative Server architecture, including client side prediction, entity interpolation, physics re-simulation, lag compensation and more!

---

> [!WARNING]
> VERY IMPORTANT! Right now MonkeNet uses its [own fork of Godot!!!](https://github.com/grazianobolla/godot/tree/daylily-zeleen/physics_space_step) (This link is for 4.7.0) for networked physics!!! Due to current limitations on Godot Physics and the Jolt module it is impossible to step the physics world manually in vanilla Godot.

## 🧩 Dependencies and .NET environment
MonkeNet requires your game project to use .NET 8, also it heavily uses [ImGui](https://github.com/ocornut/imgui) to display very important debug information, this means you will also have to install [ImGui Godot](https://github.com/pkdawson/imgui-godot) in your project, that's MonkeNet only dependency, altough I plan to remove it/make it modular in the future.

## 💾 Installation
MonkeNet is a Godot addon, to start using it copy the `addons\monke-net\` folder, paste it into your project `addons\` folder, and enable the plugin in your project settings. After this, you will have access to all MonkeNet resources.

## 📦 This Repository (Demo Project)
This repository is the developing environment for the addon, including tests and a demo project showcasing MonkeNet capabilities. If you have any trouble getting it to work, cloning this repository might be a good starting point that you can later adapt to your games requirements.
<video src="https://github.com/user-attachments/assets/3695e351-6a4a-4145-893a-d9292bdc803c" width="600px"></video>
<sup>Example recorded with 200ms lag, 5% packet loss, 10% out of order, 10% duplicated, 10% throttle in Clumsy 0.3</sup>

## 📐 Project Structure (WIP)
MonkeNet is structured in different "components" that are Nodes inside the Godot engine, these components work together to provide different functionalities. Usually for the same funcionality there is a Client component and a Server component altough they do different things. Here there are some examples:

- `ClientEntityManager.cs` handles *requesting* an entity on the server while `ServerEntityManager.cs` actually takes that request and spawns the entity.
- `ClientNetworkClock.cs` receives clock data from the server and updates its internal state, while the `ServerNetworkClock.cs` just runs a simple clock that increments each tick.

## Components (OUTDATED)
### 🐵 MonkeNet Singleton
The `MonkeNetManager` class is a singleton that can be used anywhere in your project and allows you to start either a server or a client.

### 🖥️ Client Side Components 
- Client Manager
- Entity Manager
- Network Clock
- Snapshot Interpolator
- Snapshot Rollbacker
- Input Manager

### 🖧 Server Side Components
- Server Manager
- Entity Manager
- Network Clock
- Input Receiver

### 🤝 Shared Components
- Message Serializer
- Entity Spawner
- Network Manager
- MonkeNet Config

---
