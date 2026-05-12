using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace MonkeNet.Tools.Recording;

/// <summary>
/// Animation playback scene for the CollisionMotionPlotTests harness. Reads the
/// per-scenario CSV traces written by the test and replays each as a coloured
/// capsule (player, red) + box (vehicle, blue) sequenced behind a static camera.
/// Pure presentation — no physics, no library code — so the visible motion
/// matches the CSV exactly, frame for frame.
///
/// Designed to be launched as a subprocess under Godot's --write-movie flag,
/// so the engine's built-in MovieWriter records every rendered frame into a
/// single AVI covering all scenarios end-to-end.
///
/// Invocation:
///   godot --path &lt;repo&gt; --write-movie out.avi --fixed-fps 60 --disable-vsync \
///         res://tools/recording/CollisionPlayback.tscn -- csvDir=&lt;abs CSV path&gt;
/// </summary>
public partial class CollisionPlayback : Node3D
{
    private static readonly string[] Scenarios =
        { "baseline", "listen_cb", "listen_rb", "host_cb", "host_cb_client", "host_rb", "host_rb_client" };
    private const float BodyY = -1f;
    private const int InterScenarioPauseFrames = 30; // 0.5 s at 60 fps

    private string _csvDir = "";

    public override async void _Ready()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("csvDir="))
                _csvDir = arg.Substring("csvDir=".Length);
        }
        if (string.IsNullOrEmpty(_csvDir))
        {
            GD.PrintErr("CollisionPlayback: missing `csvDir=` user arg");
            GetTree().Quit(1);
            return;
        }

        BuildBackdrop();
        // One settle frame so MovieWriter has captured something non-empty
        // before scenario meshes spawn (otherwise the first scenario's first
        // frame can land in the same render frame as the backdrop add).
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        foreach (string name in Scenarios)
        {
            string path = Path.Combine(_csvDir, $"{name}.csv");
            if (!File.Exists(path))
            {
                GD.PrintErr($"CollisionPlayback: missing trace {path}");
                continue;
            }
            await PlayScenario(name, LoadFrames(path));
            await Pause(InterScenarioPauseFrames);
        }

        GetTree().Quit();
    }

    private void BuildBackdrop()
    {
        var floor = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(30, 1, 80) },
            Position = new Vector3(0, -2.5f, -20f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.32f, 0.35f) },
        };
        AddChild(floor);

        var camera = new Camera3D
        {
            Position = new Vector3(5, 4, 8),
            Fov = 65f,
            Current = true,
        };
        // LookAt requires the node to be inside the scene tree — add first, aim after.
        AddChild(camera);
        camera.LookAt(new Vector3(0, BodyY, -15), Vector3.Up);

        var light = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-50, -30, 0),
        };
        AddChild(light);
    }

    private async Task PlayScenario(string name, List<(float playerZ, float vehicleZ, float dummyVehicleZ)> frames)
    {
        float firstPlayerZ = frames.Count > 0 ? frames[0].playerZ : 0f;
        float firstVehicleZ = frames.Count > 0 ? frames[0].vehicleZ : -3f;
        float firstDummyZ = frames.Count > 0 ? frames[0].dummyVehicleZ : firstVehicleZ;

        var player = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.5f, Height = 2f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.25f, 0.25f) },
            Position = new Vector3(0, BodyY, firstPlayerZ),
        };
        AddChild(player);

        var vehicle = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2, 1, 4) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.45f, 0.9f) },
            Position = new Vector3(0, BodyY, firstVehicleZ),
        };
        AddChild(vehicle);

        // Dummy = lag-buffered server vehicle. Rendered as a translucent wireframe-style
        // ghost offset on +X so it doesn't z-fight with the real vehicle when both
        // happen to be at the same Z.
        var dummy = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2, 1, 4) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.6f, 0.85f, 1f, 0.35f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
            Position = new Vector3(2.5f, BodyY, firstDummyZ),
        };
        AddChild(dummy);

        var label = new Label3D
        {
            Text = name,
            Position = new Vector3(0, 4, -5),
            FontSize = 96,
            Modulate = Colors.White,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        AddChild(label);

        foreach (var (playerZ, vehicleZ, dummyZ) in frames)
        {
            player.Position = new Vector3(0, BodyY, playerZ);
            vehicle.Position = new Vector3(0, BodyY, vehicleZ);
            dummy.Position = new Vector3(2.5f, BodyY, dummyZ);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        player.QueueFree();
        vehicle.QueueFree();
        dummy.QueueFree();
        label.QueueFree();
    }

    private async Task Pause(int renderFrames)
    {
        for (int i = 0; i < renderFrames; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static List<(float playerZ, float vehicleZ, float dummyVehicleZ)> LoadFrames(string path)
    {
        var result = new List<(float, float, float)>();
        var lines = File.ReadAllLines(path);
        // Skip header row at index 0; format:
        //   frame,player_z,vehicle_z,dummy_vehicle_z,vehicle_vz   (current)
        //   frame,player_z,vehicle_z,vehicle_vz                   (legacy fallback)
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 3) continue;
            float pz = float.Parse(parts[1], CultureInfo.InvariantCulture);
            float vz = float.Parse(parts[2], CultureInfo.InvariantCulture);
            float dvz = parts.Length >= 5
                ? float.Parse(parts[3], CultureInfo.InvariantCulture)
                : vz;
            result.Add((pz, vz, dvz));
        }
        return result;
    }
}
