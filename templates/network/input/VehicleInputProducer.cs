using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;

namespace YourGame;

// Reads vehicle control input each network tick. Works for both keyboard and
// analog controller. Swap this in on MonkeNetConfig.InputProducer when the
// local player enters a vehicle; restore PlayerInputProducer on exit.
//
// Scene setup: add as a child of your LocalVehicle scene.
//
// Example enter/exit wiring in GDScript:
//   MonkeNetConfig.input_producer = $VehicleInputProducer  # on enter
//   MonkeNetConfig.input_producer = $PlayerInputProducer   # on exit
public partial class VehicleInputProducer : InputProducerComponent
{
    public override void _Ready()
    {
        base._Ready(); // Registers this producer with MonkeNetConfig — do not remove.
    }

    public override IPackableElement GenerateCurrentInput()
    {
        return new VehicleInputMessage
        {
            // GetAxis returns -1..1 for both keyboard bindings and controller analog axes.
            Steering  = Input.GetAxis("steer_left",  "steer_right"),
            Throttle  = Input.GetAxis("brake",       "accelerate"),
            Brake     = Input.GetActionStrength("brake"),
            Handbrake = Input.IsActionPressed("handbrake"),
        };
    }
}
