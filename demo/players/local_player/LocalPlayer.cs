using Godot;
using ImGuiNET;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalPlayer : CharacterBody3D
{
    public override void _Process(double delta)
    {
        if (!GetTree().Root.HasNode("ImGuiRoot")) return;
        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10f, displaySize.Y - 10f), ImGuiCond.Always, new System.Numerics.Vector2(0f, 1f));
        if (ImGui.Begin("Player Information"))
        {
            try
            {
                ImGui.Text("Use `C` to free/capture your cursor.");
                ImGui.Text($"Position ({GlobalPosition.X:0.00}, {GlobalPosition.Y:0.00}, {GlobalPosition.Z:0.00})");

                int clientTicks = GetNodeOrNull<SharedPlayerMovement>("SharedPlayerMovement")?.RigidbodyContactTicks ?? 0;
                int serverTicks = LookupServerSideContactTicks();
                // "—" reads as "no host counterpart visible" (e.g. external client without a
                // listen-server running in this process).
                string serverDisplay = serverTicks >= 0 ? serverTicks.ToString() : "—";
                ImGui.Text($"Rigidbody contact ticks  client: {clientTicks}  host: {serverDisplay}");
            }
            finally { ImGui.End(); }
        }
    }

    // Host-side count is only readable when the server lives in this process (listen
    // server). Walk EntitySpawner's authoritative-entity list looking for the ServerPlayer
    // owned by this client; -1 means "not running locally." Wrapped in try/catch because
    // ClientManager.GetNetworkId() throws if the network isn't initialised yet, and an
    // exception escaping _Process between ImGui.Begin and ImGui.End would corrupt the
    // ImGui frame and hide every overlay window for the rest of the session.
    private static int LookupServerSideContactTicks()
    {
        try
        {
            var spawner = EntitySpawner.Instance;
            var cm = ClientManager.Instance;
            if (spawner == null || cm == null || !cm.IsNetworkReady) return -1;
            int myAuthority = cm.GetNetworkId();
            foreach (var entity in spawner.Entities)
            {
                // EntityType 0 = the regular CharacterBody3D player; matches LocalPlayer's
                // server twin. RigidPlayerPhysics (entity type 3) doesn't share this counter,
                // so it's intentionally skipped here.
                if (entity == null || entity.EntityType != 0 || entity.Authority != myAuthority) continue;
                var root = spawner.GetEntityRoot(entity);
                var movement = root?.GetNodeOrNull<SharedPlayerMovement>("SharedPlayerMovement");
                if (movement != null) return movement.RigidbodyContactTicks;
            }
        }
        catch { /* best-effort overlay; never break ImGui mid-frame */ }
        return -1;
    }
}
