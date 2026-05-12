using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// CSV + SVG plot writer for <see cref="MultiProcessOffsetPushTests"/>. The
/// graph shows player + cube state (position, velocity, orientation, angular
/// velocity) over the test run so the cube reconcile snap at end-of-push is
/// visible as a discontinuity in one or more panels — typically clearest in
/// linear-velocity (snaps to zero) and angular-velocity (snaps to zero) when
/// SyncSleepState re-anchors the body to the server's resting pose.
///
/// CSV columns (long-form, one row per (tick, entity)):
///   tick, mispredictionsCount, mispredictsThisRun, eid, etype,
///   x, y, z, vx, vy, vz, avx, avy, avz, qx, qy, qz, qw
///
/// SVG layout (five stacked panels, 1000x900):
///   1. world Z position (player runs −Z; cube drifts on contact)
///   2. world X position (cube spins sideways from offset contact)
///   3. linear velocity Z (snap to zero is the loudest signal of a sleep-sync re-anchor)
///   4. yaw (degrees, derived from rotation Y-axis component) — shows rotation step
///   5. angular velocity magnitude — peaks during spin, then drops; any
///      discontinuous step is a reconcile snap
///   Orange dashed verticals span all panels — every sample tick where the
///   client-side MispredictionsCount increased (i.e. a hard reconcile fired).
/// </summary>
internal static class OffsetPushPlot
{
    public static void WriteCsv(string path, List<MultiProcessMispredictTests.Sample> samples,
        int playerEid, int cubeEid, int baselineMispredicts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("tick,mispredictionsCount,mispredictsThisRun,eid,etype,x,y,z,vx,vy,vz,avx,avy,avz,qx,qy,qz,qw,vx_vis,vy_vis,vz_vis,vqx,vqy,vqz,vqw");
        foreach (var s in samples)
        {
            int delta = s.MispredictionsCount - baselineMispredicts;
            foreach (var e in s.Entities)
            {
                if (e.Id != playerEid && e.Id != cubeEid) continue;
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
                  .Append(F(e.Velocity.Z)).Append(',')
                  .Append(F(e.AngularVelocity.X)).Append(',')
                  .Append(F(e.AngularVelocity.Y)).Append(',')
                  .Append(F(e.AngularVelocity.Z)).Append(',')
                  .Append(F(e.Rotation.X)).Append(',')
                  .Append(F(e.Rotation.Y)).Append(',')
                  .Append(F(e.Rotation.Z)).Append(',')
                  .Append(F(e.Rotation.W)).Append(',')
                  .Append(F(e.VisualPosition.X)).Append(',')
                  .Append(F(e.VisualPosition.Y)).Append(',')
                  .Append(F(e.VisualPosition.Z)).Append(',')
                  .Append(F(e.VisualRotation.X)).Append(',')
                  .Append(F(e.VisualRotation.Y)).Append(',')
                  .Append(F(e.VisualRotation.Z)).Append(',')
                  .Append(F(e.VisualRotation.W)).AppendLine();
            }
        }
        File.WriteAllText(path, sb.ToString());
    }

    private struct Panel
    {
        public string Title;
        public Func<MultiProcessMispredictTests.EntityState, float> Value;
        // Optional. If non-null, an additional cube line is drawn on this panel
        // reading the visual mesh's value. Only meaningful for spatial readings
        // (position, yaw) — velocity / angular velocity are body-only physics
        // state and visual = body for those.
        public Func<MultiProcessMispredictTests.EntityState, float> VisualValue;
        public string Units;
    }

    public static void WriteSvg(string path, List<MultiProcessMispredictTests.Sample> samples,
        int playerEid, int cubeEid, int baselineMispredicts, string label)
    {
        // Five panels makes the chart tall but each panel stays readable.
        // Each panel auto-fits its Y axis to the data so a sub-cm position
        // step in one panel and a 5 m/s velocity drop in another are both
        // clearly visible.
        var panels = new Panel[]
        {
            new() { Title = "world Z position (body solid; visual dashed — should stay smooth across body snap)", Units = "m", Value = e => e.Position.Z, VisualValue = e => e.VisualPosition.Z },
            new() { Title = "world X position (body solid; visual dashed)",                                       Units = "m", Value = e => e.Position.X, VisualValue = e => e.VisualPosition.X },
            new() { Title = "linear velocity Z (body only — visual has no physics velocity)",                     Units = "m/s", Value = e => e.Velocity.Z },
            new() { Title = "yaw (body solid; visual dashed — visual yaw decays smoothly toward body)",           Units = "°",   Value = e => QuatToYawDeg(e.Rotation), VisualValue = e => QuatToYawDeg(e.VisualRotation) },
            new() { Title = "angular velocity magnitude (body only)",                                             Units = "rad/s", Value = e => e.AngularVelocity.Length() },
        };

        const int W = 1000;
        const int LeftPad = 80;
        const int RightPad = 20;
        const int TopPad = 36;
        const int PanelH = 130;
        const int PanelGap = 24;
        const int BottomPad = 36;
        // Bottom pad is doubled vs. side panels because the x-axis labels live
        // below the last panel (numeric tick values + the "tick" axis caption).
        int H = TopPad + panels.Length * PanelH + (panels.Length - 1) * PanelGap + BottomPad + 16;
        int plotW = W - LeftPad - RightPad;

        int firstTick = samples.Count > 0 ? samples[0].Tick : 0;
        int lastTick = samples.Count > 0 ? samples[samples.Count - 1].Tick : 1;
        if (lastTick == firstTick) lastTick = firstTick + 1;
        float xPerTick = plotW / (float)(lastTick - firstTick);
        float XPx(int tick) => LeftPad + (tick - firstTick) * xPerTick;

        // Pre-compute each panel's Y-axis range from the data so tight steps
        // are visible at the same time as wide swings.
        var ranges = new (float Min, float Max)[panels.Length];
        for (int p = 0; p < panels.Length; p++)
        {
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (var s in samples)
            {
                foreach (var e in s.Entities)
                {
                    if (e.Id != playerEid && e.Id != cubeEid) continue;
                    float v = panels[p].Value(e);
                    if (float.IsFinite(v))
                    {
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    // Auto-fit must include the visual trace too, otherwise a
                    // visual mid-decay sample below the body's range would
                    // clip off the bottom of the panel.
                    if (panels[p].VisualValue != null && e.Id == cubeEid)
                    {
                        float vv = panels[p].VisualValue(e);
                        if (float.IsFinite(vv))
                        {
                            if (vv < min) min = vv;
                            if (vv > max) max = vv;
                        }
                    }
                }
            }
            if (!float.IsFinite(min) || !float.IsFinite(max)) { min = -1; max = 1; }
            if (Mathf.Abs(max - min) < 1e-4f) { min -= 0.5f; max += 0.5f; }
            float pad = Mathf.Max(0.05f, (max - min) * 0.1f);
            ranges[p] = (min - pad, max + pad);
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(W).Append("\" height=\"").Append(H).Append("\" viewBox=\"0 0 ").Append(W).Append(' ').Append(H).AppendLine("\">");
        // Explicit white background. SVG defaults to transparent, which renders
        // as the host's background colour — dark grey in some viewers, making
        // axis labels and grid lines hard to read.
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\" />");
        sb.AppendLine();
        sb.AppendLine("<style>");
        sb.AppendLine(".axis { stroke: #333; stroke-width: 1; }");
        sb.AppendLine(".grid { stroke: #ddd; stroke-width: 0.5; stroke-dasharray: 2 3; }");
        sb.AppendLine(".player { stroke: #c0392b; stroke-width: 1.6; fill: none; }");
        sb.AppendLine(".cube   { stroke: #2980b9; stroke-width: 1.6; fill: none; }");
        // Cube visual mesh: lighter blue, dashed. Sits on top of the body line.
        // When the smoother is doing its job the visual line stays smooth and
        // continuous across the same x-tick where the body line discontinuously
        // jumps.
        sb.AppendLine(".cubevis{ stroke: #27ae60; stroke-width: 1.4; fill: none; stroke-dasharray: 4 2; }");
        sb.AppendLine(".mispredict { stroke: #d35400; stroke-width: 1; stroke-dasharray: 2 2; opacity: 0.7; }");
        sb.AppendLine(".text   { font: 11px sans-serif; fill: #222; }");
        sb.AppendLine(".legend { font: 11px sans-serif; }");
        sb.AppendLine(".title  { font: 12px sans-serif; fill: #222; font-weight: bold; }");
        sb.AppendLine("</style>");

        int finalMispredicts = (samples.Count > 0 ? samples[samples.Count - 1].MispredictionsCount : baselineMispredicts) - baselineMispredicts;
        sb.Append("<text x=\"").Append(LeftPad).Append("\" y=\"20\" class=\"title\">")
          .Append(EscapeXml(label)).Append("  —  mispredictions this run: ").Append(finalMispredicts).AppendLine("</text>");

        // Misprediction markers span ALL panels so a single dashed vertical
        // line correlates the same event across position, velocity, rotation.
        int totalPanelsTop = TopPad;
        int totalPanelsBottom = TopPad + panels.Length * PanelH + (panels.Length - 1) * PanelGap;
        int prevCount = baselineMispredicts;
        foreach (var s in samples)
        {
            if (s.MispredictionsCount > prevCount)
            {
                float xp = XPx(s.Tick);
                sb.Append("<line class=\"mispredict\" x1=\"").Append(F(xp)).Append("\" y1=\"").Append(totalPanelsTop)
                  .Append("\" x2=\"").Append(F(xp)).Append("\" y2=\"").Append(totalPanelsBottom).AppendLine("\" />");
                int firedInWindow = s.MispredictionsCount - prevCount;
                if (firedInWindow > 1)
                {
                    sb.Append("<text class=\"legend\" x=\"").Append(F(xp + 2)).Append("\" y=\"").Append(totalPanelsTop + 12)
                      .Append("\" fill=\"#d35400\">").Append(firedInWindow).AppendLine("</text>");
                }
            }
            prevCount = s.MispredictionsCount;
        }

        // Draw each panel.
        for (int p = 0; p < panels.Length; p++)
        {
            int yTop = TopPad + p * (PanelH + PanelGap);
            int yBottom = yTop + PanelH;
            float yPerUnit = PanelH / (ranges[p].Max - ranges[p].Min);
            float ToY(float v) => yTop + (ranges[p].Max - v) * yPerUnit;

            // Title above the panel.
            sb.Append("<text x=\"").Append(LeftPad).Append("\" y=\"").Append(yTop - 6).Append("\" class=\"title\">")
              .Append(EscapeXml(panels[p].Title)).AppendLine("</text>");

            // Grid + Y axis labels.
            DrawAxisLabels(sb, LeftPad, W - RightPad, ranges[p].Min, ranges[p].Max, ToY, panels[p].Units);

            // Cube body line first; then visual (dashed) so it overlays the
            // body and the smoothing is visible as a separate continuous line
            // across the snap tick. Player goes on top of both.
            AppendPolyline(sb, "cube", samples, cubeEid, XPx, e => ToY(panels[p].Value(e)));
            if (panels[p].VisualValue != null)
            {
                AppendPolyline(sb, "cubevis", samples, cubeEid, XPx, e => ToY(panels[p].VisualValue(e)));
            }
            AppendPolyline(sb, "player", samples, playerEid, XPx, e => ToY(panels[p].Value(e)));

            // Axes.
            DrawPanelAxes(sb, LeftPad, W - RightPad, yTop, yBottom);
        }

        // X-axis tick labels — placed under the bottom-most panel only, with
        // gridline marks at the same tick positions on every panel so each
        // sample on the chart can be cross-referenced to a tick number.
        int bottomPanelTop = TopPad + (panels.Length - 1) * (PanelH + PanelGap);
        int bottomPanelBottom = bottomPanelTop + PanelH;
        const int NumTicks = 8;
        for (int i = 0; i <= NumTicks; i++)
        {
            int tickValue = firstTick + (int)Math.Round((lastTick - firstTick) * (i / (float)NumTicks));
            float xp = XPx(tickValue);
            // Faint vertical gridline behind ALL panels for orientation.
            sb.Append("<line class=\"grid\" x1=\"").Append(F(xp)).Append("\" y1=\"").Append(TopPad)
              .Append("\" x2=\"").Append(F(xp)).Append("\" y2=\"").Append(bottomPanelBottom).AppendLine("\" />");
            // Numeric label under the bottom panel.
            sb.Append("<text class=\"text\" x=\"").Append(F(xp)).Append("\" y=\"").Append(bottomPanelBottom + 14)
              .Append("\" text-anchor=\"middle\">").Append(tickValue).AppendLine("</text>");
        }
        sb.Append("<text class=\"text\" x=\"").Append(W / 2).Append("\" y=\"").Append(H - 6).AppendLine("\" text-anchor=\"middle\">tick</text>");

        // Legend in the top-right.
        int lx = W - 180;
        int ly = TopPad + 4;
        sb.Append("<line class=\"player\" x1=\"").Append(lx).Append("\" y1=\"").Append(ly)
          .Append("\" x2=\"").Append(lx + 18).Append("\" y2=\"").Append(ly).AppendLine("\" />");
        sb.Append("<text class=\"legend\" x=\"").Append(lx + 24).Append("\" y=\"").Append(ly + 4).AppendLine("\">player</text>");
        sb.Append("<line class=\"cube\" x1=\"").Append(lx).Append("\" y1=\"").Append(ly + 14)
          .Append("\" x2=\"").Append(lx + 18).Append("\" y2=\"").Append(ly + 14).AppendLine("\" />");
        sb.Append("<text class=\"legend\" x=\"").Append(lx + 24).Append("\" y=\"").Append(ly + 18).AppendLine("\">cube (body)</text>");
        sb.Append("<line class=\"cubevis\" x1=\"").Append(lx).Append("\" y1=\"").Append(ly + 28)
          .Append("\" x2=\"").Append(lx + 18).Append("\" y2=\"").Append(ly + 28).AppendLine("\" />");
        sb.Append("<text class=\"legend\" x=\"").Append(lx + 24).Append("\" y=\"").Append(ly + 32).AppendLine("\">cube (visual)</text>");
        sb.Append("<line class=\"mispredict\" x1=\"").Append(lx).Append("\" y1=\"").Append(ly + 42)
          .Append("\" x2=\"").Append(lx + 18).Append("\" y2=\"").Append(ly + 42).AppendLine("\" />");
        sb.Append("<text class=\"legend\" x=\"").Append(lx + 24).Append("\" y=\"").Append(ly + 46).AppendLine("\">reconcile snap</text>");

        sb.AppendLine("</svg>");
        File.WriteAllText(path, sb.ToString());
    }

    // Y-axis grid + labels for a single panel.
    private static void DrawAxisLabels(StringBuilder sb, int xLeft, int xRight,
        float vMin, float vMax, Func<float, float> ToY, string units)
    {
        for (int i = 0; i <= 4; i++)
        {
            float v = vMin + (vMax - vMin) * (i / 4f);
            float y = ToY(v);
            sb.Append("<line class=\"grid\" x1=\"").Append(xLeft).Append("\" y1=\"").Append(F(y))
              .Append("\" x2=\"").Append(xRight).Append("\" y2=\"").Append(F(y)).AppendLine("\" />");
            sb.Append("<text class=\"text\" x=\"").Append(xLeft - 6).Append("\" y=\"").Append(F(y + 4))
              .Append("\" text-anchor=\"end\">").Append(v.ToString("F2", CultureInfo.InvariantCulture)).Append(' ').Append(units).AppendLine("</text>");
        }
    }

    private static void DrawPanelAxes(StringBuilder sb, int xLeft, int xRight, int yTop, int yBottom)
    {
        sb.Append("<line class=\"axis\" x1=\"").Append(xLeft).Append("\" y1=\"").Append(yTop)
          .Append("\" x2=\"").Append(xLeft).Append("\" y2=\"").Append(yBottom).AppendLine("\" />");
        sb.Append("<line class=\"axis\" x1=\"").Append(xLeft).Append("\" y1=\"").Append(yBottom)
          .Append("\" x2=\"").Append(xRight).Append("\" y2=\"").Append(yBottom).AppendLine("\" />");
    }

    private static void AppendPolyline(StringBuilder sb, string cls,
        List<MultiProcessMispredictTests.Sample> samples, int eid,
        Func<int, float> XPx, Func<MultiProcessMispredictTests.EntityState, float> YPx)
    {
        sb.Append("<polyline class=\"").Append(cls).Append("\" points=\"");
        bool any = false;
        foreach (var s in samples)
        {
            foreach (var e in s.Entities)
            {
                if (e.Id != eid) continue;
                float y = YPx(e);
                if (!float.IsFinite(y)) continue;
                if (any) sb.Append(' ');
                sb.Append(F(XPx(s.Tick))).Append(',').Append(F(y));
                any = true;
                break;
            }
        }
        sb.AppendLine("\" />");
    }

    // Extract a yaw angle (rotation about the world Y axis) from a quaternion.
    // Suitable for upright bodies rolling on a flat floor — Y-component spin is
    // the dominant rotation, so the conversion is robust enough for a plot.
    // Returns degrees in (-180, 180].
    private static float QuatToYawDeg(Quaternion q)
    {
        // Yaw = atan2(2*(w*y + x*z), 1 - 2*(y*y + z*z)) — standard Y-axis Euler
        // extraction for the convention Godot uses (Z forward, Y up).
        float sinYawCosPitch = 2f * (q.W * q.Y + q.X * q.Z);
        float cosYawCosPitch = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        return Mathf.RadToDeg(Mathf.Atan2(sinYawCosPitch, cosYawCosPitch));
    }

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
