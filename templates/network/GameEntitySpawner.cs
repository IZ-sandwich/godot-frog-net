using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Shared;

namespace YourGame;

public partial class GameEntitySpawner : EntitySpawner
{
    public enum EntityType : byte
    {
        Player,
        Prop,
        Vehicle,
    }

    // Update these paths to match your project layout.
    private const string ServerPlayerScene  = "res://scenes/network/entities/ServerPlayer.tscn";
    private const string LocalPlayerScene   = "res://scenes/network/entities/LocalPlayer.tscn";
    private const string DummyPlayerScene   = "res://scenes/network/entities/DummyPlayer.tscn";

    private const string ServerPropScene    = "res://scenes/network/entities/ServerProp.tscn";
    private const string ClientPropScene    = "res://scenes/network/entities/ClientProp.tscn";

    private const string ServerVehicleScene = "res://scenes/network/entities/ServerVehicle.tscn";
    private const string LocalVehicleScene  = "res://scenes/network/entities/LocalVehicle.tscn";
    private const string DummyVehicleScene  = "res://scenes/network/entities/DummyVehicle.tscn";

    protected override Node3D HandleEntityCreationClientSide(EntityEventMessage @event)
    {
        bool isOwned = @event.Authority == ClientManager.Instance.GetNetworkId();

        string scenePath = (EntityType)@event.EntityType switch
        {
            EntityType.Player  => isOwned ? LocalPlayerScene  : DummyPlayerScene,
            EntityType.Prop    => ClientPropScene,
            EntityType.Vehicle => isOwned ? LocalVehicleScene : DummyVehicleScene,
            _ => throw new System.Exception($"Unknown entity type: {@event.EntityType}")
        };

        return GD.Load<PackedScene>(scenePath).Instantiate<Node3D>();
    }

    protected override Node3D HandleEntityCreationServerSide(EntityEventMessage @event)
    {
        string scenePath = (EntityType)@event.EntityType switch
        {
            EntityType.Player  => ServerPlayerScene,
            EntityType.Prop    => ServerPropScene,
            EntityType.Vehicle => ServerVehicleScene,
            _ => throw new System.Exception($"Unknown entity type: {@event.EntityType}")
        };

        return GD.Load<PackedScene>(scenePath).Instantiate<Node3D>();
    }
}
