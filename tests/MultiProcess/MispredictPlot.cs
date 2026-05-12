using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// CSV + SVG plot writer for <see cref="MultiProcessMispredictTests"/>. The CSV is
/// the canonical long-form trace consumed by both the SVG renderer here and the
/// MispredictPlayback playback scene that drives the AVI video.
///
/// CSV format (long-form, one row per (tick, entity) plus a header):
///   header:  tick,mispredictionsCount,mispredictsThisRun,eid,etype,x,y,z,vx,vy,vz
///
/// SVG layout (single panel, 900x360):
///   X axis: physics tick number
///   Y axis: world Z position (player walks -Z, so the player line slopes down)
///   Red line   : player Z
///   Blue lines : each cube's Z (the tower; cubes will diverge once they get
///                knocked off the stack)
///   Orange dashed vertical: every sample tick where mispredictionsCount increased
/// </summary>
internal static class MispredictPlot
{
    public static void WriteCsv(string path, List<MultiProcessMispredictTests.Sample> samples,
        int playerEid, List<int> cubeEids, int baselineMispredicts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("tick,mispredictionsCount,mispredictsThisRun,eid,etype,x,y,z,vx,vy,vz");
        foreach (var s in samples)
        {
            int delta = s.MispredictionsCount - baselineMispredicts;
            foreach (var e in s.Entities)
            {
                sb.Append(s.Tick.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.MispredictionsCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(delta.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.Type.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(F(e.Position.X)).Append(',')
                  .Append(F(e.Position.Y)).Append(',')
                  .Append(F(e.Position.Z)).Append(',')
                  .Append(F(e.Velocity.X)).Append(',')
                  .Append(F(e.Velocity.Y)).Append(',')
                  .Append(F(e.Velocity.Z)).AppendLine();
            }
        }
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteSvg(string path, List<MultiProcessMispredictTests.Sample> samples,
        int playerEid, List<int> cubeEids, int baselineMispredicts, string label)
    {
        const int W = 900;
        const int H = 360;
        const int LeftPad = 70;
        const int RightPad = 20;
        const int TopPad = 40;
        const int BottomPad = 50;
        int plotW = W - LeftPad - RightPad;
        int plotH = H - TopPad - BottomPad;

        int firstTick = samples.Count > 0 ? samples[0].Tick : 0;
        int lastTick = samples.Count > 0 ? samples[samples.Count - 1].Tick : 1;
        if (lastTick == firstTick) lastTick = firstTick + 1;

        // Auto-fit Z range over player + cubes.
        var ids = new HashSet<int>(cubeEids) { playerEid };
        float zMin = float.PositiveInfinity, zMax = float.NegativeInfinity;
        foreach (var s in samples)
        {
            foreach (var e in s.Entities)
            {
                if (!ids.Contains(e.Id)) continue;
                if (float.IsNaN(e.Position.Z)) continue;
                if (e.Position.Z < zMin) zMin = e.Position.Z;
                if (e.Position.Z > zMax) zMax = e.Position.Z;
            }
        }
        if (!float.IsFinite(zMin) || !float.IsFinite(zMax)) { zMin = -10; zMax = 2; }
        float zPad = Mathf.Max(0.1f, (zMax - zMin) * 0.1f);
        zMin -= zPad; zMax += zPad;

        float xPerTick = plotW / (float)(lastTick - firstTick);
        float yPerZ = plotH / (zMax - zMin);
        float XPx(int tick) => LeftPad + (tick - firstTick) * xPerTick;
        float YPx(float z) => TopPad + (zMax - z) * yPerZ;

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(W).Append("\" height=\"").Append(H).Append("\" viewBox=\"0 0 ").Append(W).Append(' ').Append(H).AppendLine("\">");
        sb.AppendLine("<style>");
        sb.AppendLine(".axis { stroke: #333; stroke-width: 1; }");
        sb.AppendLine(".grid { stroke: #ddd; stroke-width: 0.5; stroke-dasharray: 2 3; }");
        sb.AppendLine(".player { stroke: #c0392b; stroke-width: 1.8; fill: none; }");
        sb.AppendLine(".cube   { stroke: #2980b9; stroke-width: 1.0; fill: none; opacity: 0.7; }");
        sb.AppendLine(".mispredict { stroke: #d35400; stroke-width: 1; stroke-dasharray: 2 2; opacity: 0.65; }");
        sb.AppendLine(".text   { font: 12px sans-serif; fill: #222; }");
        sb.AppendLine(".legend { font: 11px sans-serif; }");
        sb.AppendLine("</style>");

        int finalMispredicts = (samples.Count > 0 ? samples[samples.Count - 1].MispredictionsCount : baselineMispredicts) - baselineMispredicts;
        sb.Append("<text x=\"").Append(LeftPad).Append("\" y=\"22\" class=\"text\">").Append(EscapeXml(label));
        sb.Append("  —  mispredictions this run: ").Append(finalMispredicts).Append("  —  cubes in tower: ").Append(cubeEids.Count).AppendLine("</text>");

        // Y-axis ticks (5 of them).
        for (int i = 0; i <= 5; i++)
        {
            float z = zMin + (zMax - zMin) * (i / 5f);
            float y = YPx(z);
            sb.Append("<line class=\"grid\" x1=\"").Append(LeftPad).Append("\" y1=\"").Append(F(y))
              .Append("\" x2=\"").Append(W - RightPad).Append("\" y2=\"").Append(F(y)).AppendLine("\" />");
            sb.Append("<text class=\"text\" x=\"").Append(LeftPad - 8).Append("\" y=\"").Append(F(y + 4))
              .Append("\" text-anchor=\"end\">").Append(z.ToString("F1", CultureInfo.InvariantCulture)).AppendLine("</text>");
        }
        sb.Append("<text class=\"text\" x=\"").Append(W / 2).Append("\" y=\"").Append(H - 10).AppendLine("\" text-anchor=\"middle\">tick</text>");

        // Misprediction markers BEFORE the entity polylines so the lines overlay them.
        int prevCount = baselineMispredicts;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.MispredictionsCount > prevCount)
            {
                float x = XPx(s.Tick);
                sb.Append("<line class=\"mispredict\" x1=\"").Append(F(x)).Append("\" y1=\"").Append(TopPad)
                  .Append("\" x2=\"").Append(F(x)).Append("\" y2=\"").Append(H - BottomPad).AppendLine("\" />");
                int firedInWindow = s.MispredictionsCount - prevCount;
                if (firedInWindow > 1)
                {
                    sb.Append("<text class=\"legend\" x=\"").Append(F(x + 2)).Append("\" y=\"").Append(TopPad + 12)
                      .Append("\" fill=\"#d35400\">").Append(firedInWindow).AppendLine("</text>");
                }
            }
            prevCount = s.MispredictionsCount;
        }

        // Cube polylines (drawn first so the player line sits on top).
        foreach (int cubeEid in cubeEids) AppendPolyline(sb, "cube", samples, cubeEid, XPx, YPx);
        AppendPolyline(sb, "player", samples, playerEid, XPx, YPx);

        // Axes on top.
        sb.Append("<line class=\"axis\" x1=\"").Append(LeftPad).Append("\" y1=\"").Append(TopPad)
          .Append("\" x2=\"").Append(LeftPad).Append("\" y2=\"").Append(H - BottomPad).AppendLine("\" />");
        sb.Append("<line class=\"axis\" x1=\"").Append(LeftPad).Append("\" y1=\"").Append(H - BottomPad)
          .Append("\" x2=\"").Append(W - RightPad).Append("\" y2=\"").Append(H - BottomPad).AppendLine("\" />");

        // Legend.
        int lx = W - 160;
        int ly = TopPad + 14;
        sb.Append("<line class=\"player\" x1=\"").Append(lx).Append("\" y1=\"").Append(ly)
          .Append("\" x2=\"").Append(lx + 18).Append("\" y2=\"").Append(ly).AppendLine("\" />");
        sb.Append("<text class=\"legend\" x=\"").Append(lx + 24).Append("\" y=\"").Append(ly + 4).AppendLine("\">player Z</text>");
        sb.Append("<line class=\"cube\" x1=\"").Append(lx).Append("\" y1=\"").Append(ly + 16)
          .Append("\" x2=\"").Append(lx + 18).Append("\" y2=\"").Append(ly + 16).AppendLine("\" />");
        sb.Append("<text class=\"legend\" x=\"").Append(lx + 24).Append("\" y=\"").Append(ly + 20).AppendLine("\">cube Z</text>");
        sb.Append("<line class=\"mispredict\" x1=\"").Append(lx).Append("\" y1=\"").Append(ly + 32)
          .Append("\" x2=\"").Append(lx + 18).Append("\" y2=\"").Append(ly + 32).AppendLine("\" />");
        sb.Append("<text class=\"legend\" x=\"").Append(lx + 24).Append("\" y=\"").Append(ly + 36).AppendLine("\">misprediction</text>");

        sb.AppendLine("</svg>");
        File.WriteAllText(path, sb.ToString());
    }

    private static void AppendPolyline(StringBuilder sb, string cls,
        List<MultiProcessMispredictTests.Sample> samples, int eid,
        Func<int, float> XPx, Func<float, float> YPx)
    {
        sb.Append("<polyline class=\"").Append(cls).Append("\" points=\"");
        bool any = false;
        foreach (var s in samples)
        {
            foreach (var e in s.Entities)
            {
                if (e.Id != eid) continue;
                if (float.IsNaN(e.Position.Z)) continue;
                if (any) sb.Append(' ');
                sb.Append(F(XPx(s.Tick))).Append(',').Append(F(YPx(e.Position.Z)));
                any = true;
                break;
            }
        }
        sb.AppendLine("\" />");
    }

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
