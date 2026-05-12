using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// CSV + SVG writer for <see cref="MultiProcessClockSyncTests"/>. Each sample captures
/// the server's authoritative tick and the client's synced/raw ticks at one moment.
/// The plot exposes two views of the same trace:
///
///   * <c>WriteSvgByNetworkTick</c>   — X axis is the client's synced (network) tick.
///     Useful when correlating against MonkeLogger output (CLIENT-TICK numbers).
///   * <c>WriteSvgByWallClock</c>     — X axis is wall-clock ms since first sample.
///     Useful for "how long does sync take in real time".
///
/// Each plot draws three series:
///   – synced tick (client's GetCurrentTick) vs server tick — the two should converge.
///   – synced − server − latency  (the clock gap; the test asserts this stays small).
///   – latency in ticks (separate strip, since its scale is different).
/// </summary>
internal static class ClockSyncPlot
{
    public class Sample
    {
        public long ServerWallMs;
        public long ClientWallMs;
        public int ServerTick;
        public int ClientRawTick;
        public int ClientSyncedTick;
        public int LatencyTicks;
        public int JitterTicks;
        public int OffsetTicks;
        public int SyncWindowsApplied;
        public bool NetworkReady;
    }

    public static void WriteCsv(string path, List<Sample> samples, long t0Ms)
    {
        var sb = new StringBuilder();
        sb.AppendLine("clientWallMsRel,serverWallMsRel,serverTick,clientRawTick,clientSyncedTick,latencyTicks,jitterTicks,offsetTicks,clockGap,syncWindowsApplied,networkReady");
        foreach (var s in samples)
        {
            int gap = s.ClientSyncedTick - s.ServerTick - s.LatencyTicks;
            sb.Append((s.ClientWallMs - t0Ms).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append((s.ServerWallMs - t0Ms).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(s.ServerTick).Append(',')
              .Append(s.ClientRawTick).Append(',')
              .Append(s.ClientSyncedTick).Append(',')
              .Append(s.LatencyTicks).Append(',')
              .Append(s.JitterTicks).Append(',')
              .Append(s.OffsetTicks).Append(',')
              .Append(gap).Append(',')
              .Append(s.SyncWindowsApplied).Append(',')
              .Append(s.NetworkReady ? 1 : 0).AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    public static void WriteSvgByNetworkTick(string path, List<Sample> samples, string title)
    {
        WriteSvg(path, samples, title, useWallClock: false);
    }

    public static void WriteSvgByWallClock(string path, List<Sample> samples, string title, long t0Ms)
    {
        WriteSvg(path, samples, title, useWallClock: true, t0Ms: t0Ms);
    }

    private static void WriteSvg(string path, List<Sample> samples, string title, bool useWallClock, long t0Ms = 0)
    {
        const int W = 1100;
        const int PlotH = 240;
        const int TopPad = 60;
        const int RowGap = 60;
        const int LeftPad = 80;
        const int RightPad = 30;
        const int BottomPad = 50;
        int H = TopPad + 2 * (PlotH + RowGap) + BottomPad;
        int plotW = W - LeftPad - RightPad;

        if (samples.Count == 0)
        {
            File.WriteAllText(path, $"<svg xmlns='http://www.w3.org/2000/svg' width='{W}' height='100'><text x='10' y='30'>no samples</text></svg>");
            return;
        }

        // X domain
        double xMin, xMax;
        Func<Sample, double> xOf;
        string xLabel;
        if (useWallClock)
        {
            xOf = s => s.ClientWallMs - t0Ms;
            xLabel = "wall-clock ms since first sample";
        }
        else
        {
            xOf = s => s.ClientSyncedTick;
            xLabel = "client synced tick (CLIENT-TICK in logs)";
        }
        xMin = double.PositiveInfinity; xMax = double.NegativeInfinity;
        foreach (var s in samples)
        {
            double x = xOf(s);
            if (x < xMin) xMin = x;
            if (x > xMax) xMax = x;
        }
        if (xMax == xMin) xMax = xMin + 1;

        // Top plot: synced vs server tick (and raw tick)
        int yMinT = int.MaxValue, yMaxT = int.MinValue;
        foreach (var s in samples)
        {
            yMinT = Math.Min(yMinT, Math.Min(s.ClientSyncedTick, Math.Min(s.ServerTick, s.ClientRawTick)));
            yMaxT = Math.Max(yMaxT, Math.Max(s.ClientSyncedTick, Math.Max(s.ServerTick, s.ClientRawTick)));
        }
        Pad(ref yMinT, ref yMaxT);

        // Bottom plot: clock gap + latency
        int yMinG = int.MaxValue, yMaxG = int.MinValue;
        foreach (var s in samples)
        {
            int gap = s.ClientSyncedTick - s.ServerTick - s.LatencyTicks;
            yMinG = Math.Min(yMinG, Math.Min(gap, s.LatencyTicks));
            yMaxG = Math.Max(yMaxG, Math.Max(gap, s.LatencyTicks));
        }
        Pad(ref yMinG, ref yMaxG);

        var sb = new StringBuilder();
        sb.AppendLine($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' width='{W}' height='{H}' font-family='monospace' font-size='12'>");
        sb.AppendLine("<rect width='100%' height='100%' fill='white'/>");
        sb.AppendLine($"<text x='{W / 2}' y='24' text-anchor='middle' font-size='16' font-weight='bold'>{Escape(title)}</text>");
        sb.AppendLine($"<text x='{W / 2}' y='44' text-anchor='middle' font-size='11' fill='#444'>{samples.Count} samples — X axis: {Escape(xLabel)}</text>");

        // Subplot 1: tick traces.
        int row1 = TopPad;
        DrawAxes(sb, "tick", row1, LeftPad, W - RightPad, PlotH, xMin, xMax, yMinT, yMaxT);
        DrawSeries(sb, samples, xOf, s => s.ClientSyncedTick, "#1f77b4", LeftPad, plotW, row1, PlotH, xMin, xMax, yMinT, yMaxT, dash: "");
        DrawSeries(sb, samples, xOf, s => s.ServerTick,        "#d62728", LeftPad, plotW, row1, PlotH, xMin, xMax, yMinT, yMaxT, dash: "");
        DrawSeries(sb, samples, xOf, s => s.ClientRawTick,     "#2ca02c", LeftPad, plotW, row1, PlotH, xMin, xMax, yMinT, yMaxT, dash: "4 4");
        Legend(sb, LeftPad, row1 - 16,
            ("client synced tick", "#1f77b4", ""),
            ("server tick",        "#d62728", ""),
            ("client raw tick (no offset)", "#2ca02c", "4 4"));

        // Subplot 2: clock gap (target: stays near zero quickly) + latency.
        int row2 = row1 + PlotH + RowGap;
        DrawAxes(sb, "ticks", row2, LeftPad, W - RightPad, PlotH, xMin, xMax, yMinG, yMaxG);
        // Zero line for the gap.
        int zeroY = (int)(row2 + PlotH - (0 - yMinG) / (double)(yMaxG - yMinG) * PlotH);
        sb.AppendLine($"<line x1='{LeftPad}' y1='{zeroY}' x2='{W - RightPad}' y2='{zeroY}' stroke='#888' stroke-dasharray='2 4'/>");
        DrawSeries(sb, samples, xOf, s => s.ClientSyncedTick - s.ServerTick - s.LatencyTicks,
                   "#9467bd", LeftPad, plotW, row2, PlotH, xMin, xMax, yMinG, yMaxG, dash: "");
        DrawSeries(sb, samples, xOf, s => s.LatencyTicks, "#ff7f0e", LeftPad, plotW, row2, PlotH, xMin, xMax, yMinG, yMaxG, dash: "2 4");
        Legend(sb, LeftPad, row2 - 16,
            ("clock gap = synced − server − latency", "#9467bd", ""),
            ("avg latency (ticks)",                   "#ff7f0e", "2 4"));

        sb.AppendLine("</svg>");
        File.WriteAllText(path, sb.ToString());
    }

    private static void Pad(ref int min, ref int max)
    {
        if (min == max) { min -= 1; max += 1; return; }
        int span = max - min;
        int pad = Math.Max(1, span / 10);
        min -= pad; max += pad;
    }

    private static void DrawAxes(StringBuilder sb, string yLabel, int top, int left, int right, int height,
        double xMin, double xMax, int yMin, int yMax)
    {
        int plotW = right - left;
        sb.AppendLine($"<rect x='{left}' y='{top}' width='{plotW}' height='{height}' fill='none' stroke='#888'/>");
        sb.AppendLine($"<text x='{left - 8}' y='{top - 6}' text-anchor='end' font-weight='bold'>{Escape(yLabel)}</text>");
        for (int g = 0; g <= 5; g++)
        {
            double frac = g / 5.0;
            int y = top + (int)(frac * height);
            double v = yMax - frac * (yMax - yMin);
            sb.AppendLine($"<line x1='{left}' y1='{y}' x2='{right}' y2='{y}' stroke='#eee'/>");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0}' y='{1}' text-anchor='end'>{2:0}</text>", left - 4, y + 4, v));
        }
        for (int g = 0; g <= 6; g++)
        {
            double frac = g / 6.0;
            int x = left + (int)(frac * plotW);
            double v = xMin + frac * (xMax - xMin);
            sb.AppendLine($"<line x1='{x}' y1='{top}' x2='{x}' y2='{top + height}' stroke='#eee'/>");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0}' y='{1}' text-anchor='middle'>{2:0}</text>", x, top + height + 14, v));
        }
    }

    private static void DrawSeries(StringBuilder sb, List<Sample> samples, Func<Sample, double> xOf,
        Func<Sample, int> yOf, string color, int left, int plotW, int top, int height,
        double xMin, double xMax, int yMin, int yMax, string dash)
    {
        sb.Append($"<polyline fill='none' stroke='{color}' stroke-width='1.5'");
        if (!string.IsNullOrEmpty(dash)) sb.Append($" stroke-dasharray='{dash}'");
        sb.Append(" points='");
        bool first = true;
        foreach (var s in samples)
        {
            double xn = (xOf(s) - xMin) / (xMax - xMin);
            double yn = (yOf(s) - yMin) / (double)(yMax - yMin);
            int x = left + (int)(xn * plotW);
            int y = top + height - (int)(yn * height);
            if (!first) sb.Append(' ');
            sb.Append(x).Append(',').Append(y);
            first = false;
        }
        sb.AppendLine("'/>");
    }

    private static void Legend(StringBuilder sb, int x, int y, params (string label, string color, string dash)[] entries)
    {
        int cursor = x;
        foreach (var (label, color, dash) in entries)
        {
            string dashAttr = string.IsNullOrEmpty(dash) ? "" : $" stroke-dasharray='{dash}'";
            sb.AppendLine($"<line x1='{cursor}' y1='{y + 6}' x2='{cursor + 24}' y2='{y + 6}' stroke='{color}' stroke-width='2.5'{dashAttr}/>");
            sb.AppendLine($"<text x='{cursor + 28}' y='{y + 10}'>{Escape(label)}</text>");
            cursor += 28 + 6 + label.Length * 7 + 16;
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
