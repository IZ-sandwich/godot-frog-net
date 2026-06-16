"""Render a 3-panel SVG showing the 3 worst single-tick visRot jerks
in a S7-C3 client log, overlaying the rendered visual yaw (blue) against
the authoritative rigidbody yaw extracted from [ENTITY-RECONCILE] /
[NET-SNAP-RX] lines (red dashed). The PVB-active window is shaded yellow.

Usage:
    python plot_pvb_worst_cases.py LOG OUT.svg \
        EID1 TICK_LO1 TICK_HI1 LABEL1 \
        EID2 TICK_LO2 TICK_HI2 LABEL2 \
        EID3 TICK_LO3 TICK_HI3 LABEL3
"""
import re
import sys
import math

FRAME_RE = re.compile(
    r"\[SMOOTH-FRAME\] body=(\d+) pf=\d+ clientTick=(\d+).*"
    r"bodyRaw=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"bodyRot=\(([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"visRendered=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"visRot=\(([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)\)"
)
RECONCILE_RE = re.compile(
    r"\[ENTITY-RECONCILE\] LocalRigidProp eid=(\d+) auth=eid=\d+ "
    r"pos=\(([-\d.]+),([-\d.]+),([-\d.]+)\) vel=\([-\d.]+,[-\d.]+,[-\d.]+\) "
    r"rot=\(([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)\)"
)
SNAP_RE = re.compile(
    r"\[NET-SNAP-RX\]   state\[\d+\]=eid=(\d+) pos=\([-\d.]+,[-\d.]+,[-\d.]+\) "
    r"vel=\([-\d.]+,[-\d.]+,[-\d.]+\) rot=\(([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)\)"
)
PVB_START_RE = re.compile(r"\[SMOOTH-PVB-START\] body=(\d+)")
PVB_END_RE = re.compile(r"\[SMOOTH-PVB-END\] body=(\d+)")
TICK_RE = re.compile(r"\[CLIENT-TICK\] tick=(\d+) dt")


def parse(path, target_eid, tick_lo, tick_hi):
    """Both visRot and body.rot come from the same SMOOTH-FRAME line so the
    two trajectories are sampled at identical instants on the CLIENT — no
    interp-delay artifact from comparing server-snap rotations against the
    client body's rendered visual."""
    frames = []
    body_rot_at = {}
    pvb_starts = []
    pvb_ends = []
    cur_tick = 0
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            t = TICK_RE.search(line)
            if t:
                cur_tick = int(t.group(1))
                continue
            m = FRAME_RE.search(line)
            if m and int(m.group(1)) == target_eid:
                tick = int(m.group(2))
                if tick_lo <= tick <= tick_hi:
                    bodyRot = tuple(float(m.group(i)) for i in (6, 7, 8, 9))
                    visRot = tuple(float(m.group(i)) for i in (13, 14, 15, 16))
                    frames.append((tick, visRot))
                    body_rot_at[tick] = bodyRot
                continue
            m = PVB_START_RE.search(line)
            if m and int(m.group(1)) == target_eid and tick_lo <= cur_tick <= tick_hi:
                pvb_starts.append(cur_tick)
                continue
            m = PVB_END_RE.search(line)
            if m and int(m.group(1)) == target_eid and tick_lo <= cur_tick <= tick_hi:
                pvb_ends.append(cur_tick)
                continue
    return frames, body_rot_at, pvb_starts, pvb_ends


def quat_to_yaw_deg(q):
    x, y, z, w = q
    yaw = math.atan2(2 * (w * y + x * z), 1 - 2 * (y * y + z * z))
    return math.degrees(yaw)


def render(path, out_path, panels_spec):
    """panels_spec: list of (eid, tick_lo, tick_hi, label_suffix)"""
    W, H = 1500, 1000
    PADL, PADR, PADT, PADB = 80, 30, 60, 60
    n = len(panels_spec)
    gap = 30
    plot_w = W - PADL - PADR
    plot_h = (H - PADT - PADB - gap * (n - 1)) // n

    panels = []
    for eid, lo, hi, label in panels_spec:
        data = parse(path, eid, lo, hi)
        panels.append((eid, lo, hi, label, data))

    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="{}" height="{}" font-family="monospace" font-size="12">'.format(W, H)]
    out.append('<rect width="{}" height="{}" fill="#fdfdfd"/>'.format(W, H))
    out.append('<text x="{}" y="26" font-size="17" text-anchor="middle" font-weight="bold">Top 3 visRot jerks - S7-C3 with PVB rotation slerp + 3x aggression</text>'.format(W // 2))
    out.append('<text x="{}" y="44" font-size="11" text-anchor="middle" fill="#666">blue=rendered visual yaw, red dashed=authoritative rigidbody yaw, yellow=PVB active</text>'.format(W // 2))

    for i, (eid, lo, hi, label, data) in enumerate(panels):
        frames, body_rot_at, pvb_starts, pvb_ends = data
        top = PADT + i * (plot_h + gap)

        ys_vals = [quat_to_yaw_deg(q) for _, q in frames]
        for t, q in body_rot_at.items():
            if lo <= t <= hi:
                ys_vals.append(quat_to_yaw_deg(q))
        if not ys_vals:
            ys_vals = [-180, 180]
        y_min, y_max = min(ys_vals), max(ys_vals)
        if y_max - y_min < 1:
            y_max = y_min + 1
        pad = (y_max - y_min) * 0.1
        y_min -= pad
        y_max += pad

        def xs(tick):
            return PADL + (tick - lo) / (hi - lo) * plot_w

        def ys(yaw):
            return top + plot_h - (yaw - y_min) / (y_max - y_min) * plot_h

        out.append('<rect x="{}" y="{}" width="{}" height="{}" fill="white" stroke="#bbb"/>'.format(PADL, top, plot_w, plot_h))
        out.append('<text x="{}" y="{}" font-weight="bold">{}: eid={} ticks {}-{}</text>'.format(PADL, top - 8, label, eid, lo, hi))

        if pvb_starts and pvb_ends:
            s = min(pvb_starts); e = max(pvb_ends)
            out.append('<rect x="{:.1f}" y="{}" width="{:.1f}" height="{}" fill="#fff2cc" opacity="0.6"/>'.format(xs(s), top, xs(e) - xs(s), plot_h))
            out.append('<text x="{:.1f}" y="{}" font-size="11" fill="#a07000">PVB active</text>'.format(xs(s) + 4, top + 14))

        for j in range(6):
            y_val = y_min + (y_max - y_min) * j / 5
            y_px = top + plot_h - (y_val - y_min) / (y_max - y_min) * plot_h
            out.append('<line x1="{}" y1="{:.1f}" x2="{}" y2="{:.1f}" stroke="#eee"/>'.format(PADL, y_px, PADL + plot_w, y_px))
            out.append('<text x="{}" y="{:.1f}" text-anchor="end" fill="#666">{:+.1f}deg</text>'.format(PADL - 6, y_px + 3, y_val))

        step = max(5, (hi - lo) // 14)
        for tick in range(lo, hi + 1, step):
            x_px = xs(tick)
            out.append('<line x1="{:.1f}" y1="{}" x2="{:.1f}" y2="{}" stroke="#888"/>'.format(x_px, top + plot_h, x_px, top + plot_h + 4))
            out.append('<text x="{:.1f}" y="{}" text-anchor="middle" fill="#666">{}</text>'.format(x_px, top + plot_h + 18, tick))

        body_pts = sorted([(t, q) for t, q in body_rot_at.items()])
        if body_pts:
            pts = [(xs(t), ys(quat_to_yaw_deg(q))) for t, q in body_pts]
            d = 'M ' + ' L '.join('{:.1f},{:.1f}'.format(x, y) for x, y in pts)
            out.append('<path d="{}" stroke="#c00" stroke-width="1.5" fill="none" stroke-dasharray="4 3"/>'.format(d))
            for x, y in pts:
                out.append('<circle cx="{:.1f}" cy="{:.1f}" r="1.5" fill="#c00"/>'.format(x, y))

        if frames:
            pts = [(xs(t), ys(quat_to_yaw_deg(q))) for t, q in frames]
            d = 'M ' + ' L '.join('{:.1f},{:.1f}'.format(x, y) for x, y in pts)
            out.append('<path d="{}" stroke="#06c" stroke-width="2" fill="none"/>'.format(d))
            for x, y in pts:
                out.append('<circle cx="{:.1f}" cy="{:.1f}" r="2.2" fill="#06c"/>'.format(x, y))

    out.append('</svg>')
    with open(out_path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(out))
    print('wrote {}'.format(out_path))


if __name__ == '__main__':
    if len(sys.argv) < 3 or (len(sys.argv) - 3) % 4 != 0:
        print('usage: plot_pvb_worst_cases.py LOG OUT.svg [EID LO HI LABEL]...', file=sys.stderr)
        sys.exit(1)
    log = sys.argv[1]
    out = sys.argv[2]
    rest = sys.argv[3:]
    panels = []
    for i in range(0, len(rest), 4):
        eid = int(rest[i]); lo = int(rest[i + 1]); hi = int(rest[i + 2]); label = rest[i + 3]
        panels.append((eid, lo, hi, label))
    render(log, out, panels)
