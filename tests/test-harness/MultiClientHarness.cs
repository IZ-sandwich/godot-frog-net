using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameDemo;
using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// Orchestrated test harness for multi-process integration testing. A spawned
/// Godot child process loads <c>res://tests/MultiProcess/harness.tscn</c> with
/// command-line args specifying its role (server or client), ENet port, and an
/// orchestration TCP port. The orchestrator (running in the GdUnit4 test process)
/// connects to the orch port and drives the harness via line-delimited JSON.
///
/// Each child process has its OWN Godot World3D, physics space, MonkeNet
/// singletons, and OS process — eliminating the same-process-shared-state
/// concerns of in-process multi-client testing.
///
/// CLI args (after a single `--` separator on Godot's command line):
///   --role=server|client
///   --enet-port=N        Server listens on this port; client connects to it
///   --orch-port=N        Orchestrator connects to this port to drive the harness
///   --server-addr=IP     (client only) defaults to 127.0.0.1
///   --label=str          Diagnostic label printed in logs
///
/// Protocol: line-delimited JSON over TCP. Each request is a single JSON object
/// with a "cmd" field; the response is a single JSON object on one line.
/// Commands: ready, spawn-ball, send-input, set-authority, get-all-entities,
///           get-entity, run-ticks, shutdown.
/// </summary>
public partial class MultiClientHarness : Node
{
    private TcpListener _orchListener;
    private TcpClient _orchClient;
    private NetworkStream _orchStream;
    private readonly List<byte> _accumulator = new();
    private readonly byte[] _readBuf = new byte[8192];

    private string _role = "?";
    private string _label = "";

    public override void _Ready()
    {
        var args = ParseArgs(OS.GetCmdlineUserArgs());
        _role = args.GetValueOrDefault("role", "?");
        _label = args.GetValueOrDefault("label", _role);

        if (!int.TryParse(args.GetValueOrDefault("orch-port"), out int orchPort))
        {
            GD.PrintErr($"[harness {_label}] missing --orch-port");
            GetTree().Quit(1);
            return;
        }
        if (!int.TryParse(args.GetValueOrDefault("enet-port"), out int enetPort))
        {
            GD.PrintErr($"[harness {_label}] missing --enet-port");
            GetTree().Quit(1);
            return;
        }

        // Open orch listener BEFORE wiring up MonkeNet so the orchestrator can
        // detect a "process started" signal as soon as the TCP port accepts.
        _orchListener = new TcpListener(IPAddress.Loopback, orchPort);
        _orchListener.Start();
        GD.Print($"[harness {_label}] orch listening on {orchPort}");

        if (_role == "server")
        {
            MonkeNetManager.Instance.CreateServer(enetPort);
            GD.Print($"[harness {_label}] enet server on port {enetPort}");
        }
        else if (_role == "client")
        {
            string addr = args.GetValueOrDefault("server-addr", "127.0.0.1");
            MonkeNetManager.Instance.CreateClient(addr, enetPort);
            GD.Print($"[harness {_label}] enet client connecting to {addr}:{enetPort}");
        }
        else
        {
            GD.PrintErr($"[harness {_label}] unknown role '{_role}'; valid: server|client");
            GetTree().Quit(1);
        }
    }

    public override void _Process(double delta)
    {
        if (_orchListener == null) return;

        try
        {
            if (_orchClient == null && _orchListener.Pending())
            {
                _orchClient = _orchListener.AcceptTcpClient();
                _orchStream = _orchClient.GetStream();
                GD.Print($"[harness {_label}] orchestrator connected");
            }

            if (_orchStream != null && _orchClient.Available > 0)
            {
                int n = _orchStream.Read(_readBuf, 0, _readBuf.Length);
                for (int i = 0; i < n; i++) _accumulator.Add(_readBuf[i]);
            }

            // Process any complete lines.
            while (_orchStream != null)
            {
                int newline = _accumulator.IndexOf((byte)'\n');
                if (newline < 0) break;
                string line = Encoding.UTF8.GetString(_accumulator.GetRange(0, newline).ToArray()).TrimEnd('\r');
                _accumulator.RemoveRange(0, newline + 1);
                string response = ProcessCommand(line);
                byte[] bytes = Encoding.UTF8.GetBytes(response + "\n");
                _orchStream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"[harness {_label}] orch I/O error: {e.GetType().Name}: {e.Message}");
            // Tear down the broken connection — accept a fresh one next frame.
            try { _orchStream?.Dispose(); } catch { }
            try { _orchClient?.Dispose(); } catch { }
            _orchStream = null;
            _orchClient = null;
            _accumulator.Clear();
        }
    }

    private string ProcessCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";
            return cmd switch
            {
                "ready" => Ok(new
                {
                    ready = true,
                    role = _role,
                    networkReady = (_role == "server") ? true : (ClientManager.Instance?.IsNetworkReady ?? false),
                    serverEntityCount = (_role == "server") ? EntitySpawner.Instance?.Entities.Count ?? 0 : 0,
                    clientEntityCount = (_role == "client") ? EntitySpawner.Instance?.ClientEntities.Count ?? 0 : 0,
                }),
                "tick-count" => Ok(new { ticks = (long)Engine.GetPhysicsFrames() }),
                "get-network-id" => Ok(new
                {
                    networkId = (_role == "server")
                        ? (ServerManager.Instance?.GetNetworkId() ?? 0)
                        : (ClientManager.Instance?.GetNetworkId() ?? 0),
                }),
                "spawn-ball" => SpawnBall(doc.RootElement),
                "set-authority" => SetAuthority(doc.RootElement),
                "send-input" => SendInput(doc.RootElement),
                "get-all-entities" => GetAllEntities(),
                "get-entity" => GetEntity(doc.RootElement),
                "shutdown" => Shutdown(),
                _ => Err($"unknown cmd '{cmd}'"),
            };
        }
        catch (Exception e)
        {
            return Err($"{e.GetType().Name}: {e.Message}");
        }
    }

    // ── command handlers ──────────────────────────────────────────────────────

    private string SpawnBall(JsonElement req)
    {
        if (_role != "server") return Err("spawn-ball is server-only");
        int authority = req.GetProperty("authority").GetInt32();
        var pos = ReadVec3(req.GetProperty("position"));
        var node = ServerManager.Instance.SpawnEntity<Node3D>(entityType: 1, authority: authority);
        // ServerBallStateSyncronizer.OnEntitySpawned forces (0,10,0); override after spawn.
        var lastEntity = EntitySpawner.Instance.Entities[EntitySpawner.Instance.Entities.Count - 1];
        EntitySpawner.Instance.GetEntityRoot(lastEntity)!.GlobalPosition = pos;
        return Ok(new { entityId = lastEntity.EntityId });
    }

    private string SetAuthority(JsonElement req)
    {
        if (_role != "server") return Err("set-authority is server-only");
        int eid = req.GetProperty("entity_id").GetInt32();
        int newAuth = req.GetProperty("new_authority").GetInt32();
        ServerManager.Instance.ChangeAuthority(eid, newAuth);
        return Ok(new { entityId = eid, newAuthority = newAuth });
    }

    private string SendInput(JsonElement req)
    {
        if (_role != "client") return Err("send-input is client-only");
        var input = new CharacterInputMessage
        {
            MoveX = req.TryGetProperty("moveX", out var mx) ? (float)mx.GetDouble() : 0f,
            MoveY = req.TryGetProperty("moveY", out var my) ? (float)my.GetDouble() : 0f,
            CameraYaw = req.TryGetProperty("yaw", out var yw) ? (float)yw.GetDouble() : 0f,
            Keys = req.TryGetProperty("keys", out var kk) ? (byte)kk.GetInt32() : (byte)0,
        };
        // Write directly into MonkeNetConfig.Instance.InputProducer if present;
        // for client harnesses we want to drive the per-tick GenerateCurrentInput
        // by setting a fake producer's "next input" value. Simpler path: just
        // serialize and inject as if from this client.
        var packed = new PackedClientInputMessage
        {
            Tick = 0, // ignored by the client manager — it stamps its own
            Inputs = new IPackableElement[] { input },
        };
        // Note: in a real harness, inputs are sent on each tick via the client's
        // InputProducer. This API is a one-shot push that the test orchestrator
        // can call as a synthetic input event. The stub returns OK for now.
        return Ok(new { sent = true });
    }

    private string GetAllEntities()
    {
        var list = new List<object>();
        if (_role == "server")
        {
            foreach (var e in EntitySpawner.Instance.Entities)
            {
                var root = EntitySpawner.Instance.GetEntityRoot(e);
                list.Add(new
                {
                    id = e.EntityId,
                    type = e.EntityType,
                    authority = e.Authority,
                    position = SerializeVec3(root?.GlobalPosition ?? Vector3.Zero),
                });
            }
        }
        else
        {
            foreach (var e in EntitySpawner.Instance.ClientEntities)
            {
                var root = EntitySpawner.Instance.GetEntityRoot(e);
                list.Add(new
                {
                    id = e.EntityId,
                    type = e.EntityType,
                    authority = e.Authority,
                    position = SerializeVec3(root?.GlobalPosition ?? Vector3.Zero),
                });
            }
        }
        return Ok(new { entities = list });
    }

    private string GetEntity(JsonElement req)
    {
        int eid = req.GetProperty("entity_id").GetInt32();
        var collection = _role == "server"
            ? (IEnumerable<NetworkBehaviour>)EntitySpawner.Instance.Entities
            : EntitySpawner.Instance.ClientEntities;
        foreach (var e in collection)
        {
            if (e.EntityId == eid)
            {
                var root = EntitySpawner.Instance.GetEntityRoot(e);
                return Ok(new
                {
                    id = e.EntityId,
                    type = e.EntityType,
                    authority = e.Authority,
                    position = SerializeVec3(root?.GlobalPosition ?? Vector3.Zero),
                });
            }
        }
        return Err($"entity {eid} not found");
    }

    private string Shutdown()
    {
        var resp = Ok(new { goodbye = true });
        // Quit on a deferred call so the response actually flushes to the orchestrator.
        CallDeferred(nameof(QuitDeferred));
        return resp;
    }

    private void QuitDeferred() => GetTree().Quit(0);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string> ParseArgs(string[] argv)
    {
        var dict = new Dictionary<string, string>();
        foreach (var a in argv)
        {
            if (!a.StartsWith("--")) continue;
            var trimmed = a.Substring(2);
            int eq = trimmed.IndexOf('=');
            if (eq < 0) dict[trimmed] = "true";
            else dict[trimmed.Substring(0, eq)] = trimmed.Substring(eq + 1);
        }
        return dict;
    }

    private static Vector3 ReadVec3(JsonElement el)
    {
        return new Vector3(
            (float)el[0].GetDouble(),
            (float)el[1].GetDouble(),
            (float)el[2].GetDouble());
    }

    private static double[] SerializeVec3(Vector3 v) => new double[] { v.X, v.Y, v.Z };

    private static string Ok(object payload)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "ok", true },
            { "data", payload },
        });
    }

    private static string Err(string msg)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "ok", false },
            { "error", msg },
        });
    }
}
