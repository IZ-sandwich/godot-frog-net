"""Visualize the player's push of a 3-cube stack on S3-CubeStackPush C0.

Plots position + velocity for player + each cube, with mispredictions and
tier-switch events overlaid as vertical markers. Used to investigate whether
mispredictions on a clean (C0) network cause visible snap-back artefacts on
the player.

Usage: python plot_stackpush_mispredicts.py LOG OUT.svg [TICK_LO TICK_HI]
"""
import re
import sys
import math


PRED_REG = re.compile(
    r"\[PRED-REG\] tick=(\d+) eid=(\d+) input=[^\]]*?pos=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"vel=\(([-\d.]+),([-\d.]+),([-\d.]+)\)"
)
MISPRED = re.compile(
    r"\[PRED-CHECK\] tick=(\d+) eid=(\d+) MISPREDICTED -> rollback \(tier=(\w+), class=([\w-]+)\)"
)
TIER = re.compile(r"\[TIER-SWITCH\] tick=(\d+) eid=(\d+) (\S+)")
RECONCILE = re.compile(
    r"\[ENTITY-RECONCILE\] LocalRigidProp eid=(\d+).*?pos=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"vel=\(([-\d.]+),([-\d.]+),([-\d.]+)\)"
)
SNAP = re.compile(r"\[PRED-PVB-OVERFLOW-ENTITY\] tick=(\d+) eid=(\d+)")
CLIENT_TICK = re.compile(r"\[CLIENT-TICK\] tick=(\d+)")


def parse(path, lo, hi):
    series = {}  # eid -> list of (tick, pos.z, vel.z, vel.mag)
    mispreds = []  # (tick, eid, class)
    tiers = []  # (tick, eid, direction)
    reconciles = []  # (tick, eid, pos.z, vel.z)
    snaps = []  # (client_tick, eid)
    cur_client_tick = 0
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            m = PRED_REG.search(line)
            if m:
                tick = int(m.group(1)); eid = int(m.group(2))
                if not (lo <= tick <= hi):
                    continue
                z = float(m.group(5))
                vz = float(m.group(8))
                vmag = math.sqrt(float(m.group(6))**2 + float(m.group(7))**2 + float(m.group(8))**2)
                series.setdefault(eid, []).append((tick, z, vz, vmag))
                continue
            m = MISPRED.search(line)
            if m:
                tick = int(m.group(1))
                if lo <= tick <= hi:
                    mispreds.append((tick, int(m.group(2)), m.group(4)))
                continue
            m = TIER.search(line)
            if m:
                tick = int(m.group(1))
                if lo <= tick <= hi:
                    tiers.append((tick, int(m.group(2)), m.group(3)))
                continue
            m = RECONCILE.search(line)
            if m:
                # Reconcile fires INSIDE a rollback at the rollback tick; the
                # tick in the line is the resim-loop tick which doesn't match
                # the log-line wallclock. Skip the tick filter here — the
                # caller will draw these as markers and the count is what
                # matters, not the position.
                eid = int(m.group(1))
                reconciles.append((eid, float(m.group(3)), float(m.group(6))))
                continue
            m = CLIENT_TICK.search(line)
            if m:
                cur_client_tick = int(m.group(1))
                continue
            m = SNAP.search(line)
            if m:
                # PRED-PVB-OVERFLOW-ENTITY's "tick" field is the snapshot
                # tick (delayed by latency), not the client's current tick
                # — use the most recent CLIENT-TICK marker for the wall-time
                # position so the marker lines up with the player's actual
                # position trace on the plot.
                if lo <= cur_client_tick <= hi:
                    snaps.append((cur_client_tick, int(m.group(2))))
                continue
    return series, mispreds, tiers, reconciles, snaps


def render(path, lo, hi, out_path):
    series, mispreds, tiers, reconciles, snaps = parse(path, lo, hi)

    W, H = 1600, 900
    PADL, PADR, PADT, PADB = 80, 240, 60, 50
    plot_w = W - PADL - PADR
    gap = 24
    plot_h = (H - PADT - PADB - gap) // 2

    # collect y ranges
    all_z = []
    all_v = []
    for eid, samples in series.items():
        for t, z, vz, vm in samples:
            all_z.append(z); all_v.append(vz)
    if not all_z:
        print('no data in range', file=sys.stderr); sys.exit(2)
    z_min, z_max = min(all_z), max(all_z)
    if z_max - z_min < 0.1: z_max = z_min + 0.1
    z_pad = (z_max - z_min) * 0.1
    z_min -= z_pad; z_max += z_pad
    v_min, v_max = min(all_v), max(all_v)
    if v_max - v_min < 0.1: v_max = v_min + 0.1
    v_pad = (v_max - v_min) * 0.1
    v_min -= v_pad; v_max += v_pad

    def xs(t): return PADL + (t - lo) / (hi - lo) * plot_w

    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="{}" height="{}" font-family="monospace" font-size="11">'.format(W, H)]
    out.append('<rect width="{}" height="{}" fill="#fdfdfd"/>'.format(W, H))
    out.append('<text x="{}" y="22" font-size="15" text-anchor="middle" font-weight="bold">S3-CubeStackPush C0 - player (eid=1) + stack (eid=2,3,4): mispredicts during push, ticks {}-{}</text>'.format(W//2, lo, hi))

    panels = [
        ('Z position (m) - forward push direction (more negative = further pushed)', 'z', z_min, z_max, PADT),
        ('Z velocity (m/s) - look for non-smooth player velocity = snap-back evidence', 'v', v_min, v_max, PADT + plot_h + gap),
    ]

    colors = {1: '#c00', 2: '#06c', 3: '#080', 4: '#a060c0'}
    labels = {1: 'eid=1 (PLAYER)', 2: 'eid=2 bottom cube', 3: 'eid=3 middle cube', 4: 'eid=4 top cube'}

    for title, axis, ymin, ymax, top in panels:
        def ys(y, ymin=ymin, ymax=ymax, top=top):
            return top + plot_h - (y - ymin) / (ymax - ymin) * plot_h

        out.append('<rect x="{}" y="{}" width="{}" height="{}" fill="white" stroke="#bbb"/>'.format(PADL, top, plot_w, plot_h))
        out.append('<text x="{}" y="{}" font-weight="bold">{}</text>'.format(PADL, top - 6, title))

        # Snap-to-auth markers (purple thin) - rendered FIRST so mispredict
        # markers paint on top. Snap-to-auth fires per-snapshot in C3/C4 (one
        # per snapshot, ~20 Hz) so collapse same-tick events into a single
        # marker; otherwise C4 fills the panel with overlapping lines.
        snap_ticks_seen = set()
        for tick, eid in snaps:
            if eid != 1: continue  # only show player snap-to-auth so axis doesn't blur
            if tick in snap_ticks_seen: continue
            snap_ticks_seen.add(tick)
            out.append('<line x1="{:.1f}" y1="{}" x2="{:.1f}" y2="{}" stroke="#a060c0" stroke-width="0.6" opacity="0.5"/>'.format(
                xs(tick), top, xs(tick), top + plot_h))

        # Mispredict markers (red vertical lines, taller for player)
        for tick, eid, cls in mispreds:
            col = '#c00' if eid == 1 else '#08e'
            opa = 0.65 if eid == 1 else 0.35
            out.append('<line x1="{:.1f}" y1="{}" x2="{:.1f}" y2="{}" stroke="{}" stroke-width="1" opacity="{}" stroke-dasharray="3 2"/>'.format(
                xs(tick), top, xs(tick), top + plot_h, col, opa))
            if axis == 'z':
                out.append('<text x="{:.1f}" y="{}" font-size="9" fill="{}" transform="rotate(-90 {:.1f},{}) translate(2, 0)">mispred eid={}</text>'.format(
                    xs(tick), top + 12, col, xs(tick), top + 12, eid))

        # Tier-switch markers (orange for upgrade, blue for downgrade)
        for tick, eid, direction in tiers:
            if eid == 1: continue
            col = '#f80' if 'Interpolate->Resim' in direction else '#06f'
            out.append('<line x1="{:.1f}" y1="{}" x2="{:.1f}" y2="{}" stroke="{}" stroke-width="0.5" opacity="0.3"/>'.format(
                xs(tick), top, xs(tick), top + plot_h, col))

        # Gridlines + Y labels
        for i in range(5):
            yv = ymin + (ymax - ymin) * i / 4
            yp = ys(yv)
            out.append('<line x1="{}" y1="{:.1f}" x2="{}" y2="{:.1f}" stroke="#eee"/>'.format(PADL, yp, PADL + plot_w, yp))
            out.append('<text x="{}" y="{:.1f}" text-anchor="end" fill="#666">{:+.2f}</text>'.format(PADL - 4, yp + 3, yv))

        # X ticks every 20
        for tick in range(((lo // 20) + 1) * 20, hi + 1, 20):
            xp = xs(tick)
            out.append('<line x1="{:.1f}" y1="{}" x2="{:.1f}" y2="{}" stroke="#888"/>'.format(xp, top + plot_h, xp, top + plot_h + 3))
            out.append('<text x="{:.1f}" y="{}" text-anchor="middle" fill="#666">{}</text>'.format(xp, top + plot_h + 14, tick))

        # Data lines per eid
        for eid in sorted(series.keys()):
            samples = series[eid]
            if not samples: continue
            col = colors.get(eid, '#888')
            pts = []
            for t, z, vz, vm in samples:
                val = z if axis == 'z' else vz
                pts.append((xs(t), ys(val)))
            # break the line at large gaps (>5 ticks)
            segs = [[pts[0]]]
            prev_t = samples[0][0]
            for i in range(1, len(samples)):
                if samples[i][0] - prev_t > 5:
                    segs.append([pts[i]])
                else:
                    segs[-1].append(pts[i])
                prev_t = samples[i][0]
            for seg in segs:
                if len(seg) < 2: continue
                d = 'M ' + ' L '.join('{:.1f},{:.1f}'.format(x, y) for x, y in seg)
                # Player gets a thicker line so it stands out
                w = 2.2 if eid == 1 else 1.4
                out.append('<path d="{}" stroke="{}" stroke-width="{}" fill="none"/>'.format(d, col, w))

    # legend
    lx = PADL + plot_w + 20
    ly = PADT + 10
    out.append('<text x="{}" y="{}" font-weight="bold">Legend</text>'.format(lx, ly))
    for i, eid in enumerate(sorted(colors.keys())):
        y = ly + 20 + i * 18
        col = colors[eid]
        out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="{}" stroke-width="2"/>'.format(lx, y, lx + 20, y, col))
        out.append('<text x="{}" y="{}">{}</text>'.format(lx + 26, y + 4, labels[eid]))

    y0 = ly + 20 + len(colors) * 18 + 10
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#c00" stroke-width="1" stroke-dasharray="3 2"/>'.format(lx, y0, lx + 20, y0))
    out.append('<text x="{}" y="{}">player mispredict</text>'.format(lx + 26, y0 + 4))
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#08e" stroke-width="1" stroke-dasharray="3 2"/>'.format(lx, y0 + 18, lx + 20, y0 + 18))
    out.append('<text x="{}" y="{}">cube mispredict</text>'.format(lx + 26, y0 + 22))
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#f80" stroke-width="1.5"/>'.format(lx, y0 + 36, lx + 20, y0 + 36))
    out.append('<text x="{}" y="{}">cube upgrade -&gt;Resim</text>'.format(lx + 26, y0 + 40))
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#06f" stroke-width="1.5"/>'.format(lx, y0 + 54, lx + 20, y0 + 54))
    out.append('<text x="{}" y="{}">cube downgrade -&gt;Interp</text>'.format(lx + 26, y0 + 58))
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#a060c0" stroke-width="1.5"/>'.format(lx, y0 + 72, lx + 20, y0 + 72))
    out.append('<text x="{}" y="{}">player snap-to-auth</text>'.format(lx + 26, y0 + 76))

    # counts summary
    y1 = y0 + 90
    p_mispreds = [m for m in mispreds if m[1] == 1]
    c_mispreds = [m for m in mispreds if m[1] != 1]
    out.append('<text x="{}" y="{}" font-weight="bold">Mispredict counts</text>'.format(lx, y1))
    out.append('<text x="{}" y="{}">player: {} mispred</text>'.format(lx, y1 + 18, len(p_mispreds)))
    out.append('<text x="{}" y="{}">cubes:  {} mispred</text>'.format(lx, y1 + 36, len(c_mispreds)))
    p_snaps = set(t for t, e in snaps if e == 1)
    out.append('<text x="{}" y="{}">player snap-to-auth: {}</text>'.format(lx, y1 + 54, len(p_snaps)))
    out.append('<text x="{}" y="{}" font-size="10">classes:</text>'.format(lx, y1 + 56))
    classes = {}
    for tick, eid, cls in mispreds:
        classes[cls] = classes.get(cls, 0) + 1
    for i, (k, v) in enumerate(sorted(classes.items(), key=lambda kv: -kv[1])):
        out.append('<text x="{}" y="{}" font-size="10">- {}: {}</text>'.format(lx, y1 + 74 + i * 14, k, v))

    out.append('</svg>')
    with open(out_path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(out))
    print('wrote', out_path)
    print('player mispreds:', [(t, c) for t, e, c in p_mispreds])
    print('cube mispreds:', [(t, e, c) for t, e, c in c_mispreds])


if __name__ == '__main__':
    path = sys.argv[1]
    out = sys.argv[2]
    lo = int(sys.argv[3]) if len(sys.argv) > 3 else 200
    hi = int(sys.argv[4]) if len(sys.argv) > 4 else 700
    render(path, lo, hi, out)
