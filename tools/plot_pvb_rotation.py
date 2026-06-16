"""Compare visRot vs body.rot trajectories around a PVB window for a single cube.

Reads two S7-C3 client logs (before/after the PVB-rotation slerp fix), extracts
[SMOOTH-FRAME] visRot and the most-recent [ENTITY-RECONCILE] body rotation for
a chosen eid, and emits a two-panel SVG plotting yaw vs clientTick. Before-fix
the visRot line is flat across the PVB window while body.rot curves through
the kick rotation; after-fix the visRot line should track body.rot smoothly.

Usage:
    python plot_pvb_rotation.py <before.log> <after.log> <eid> <tick_lo> <tick_hi> <out.svg>
"""
import re
import sys
import math

FRAME_RE = re.compile(
    r"\[SMOOTH-FRAME\] body=(\d+) pf=\d+ clientTick=(\d+).*"
    r"bodyRaw=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
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
    frames = []
    body_rot_at = {}
    pvb_starts = []
    pvb_ends = []
    cur_body_rot = None
    cur_tick = 0
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            t = TICK_RE.search(line)
            if t:
                cur_tick = int(t.group(1))
                if cur_body_rot is not None:
                    body_rot_at[cur_tick] = cur_body_rot
                continue
            m = RECONCILE_RE.search(line)
            if m and int(m.group(1)) == target_eid:
                cur_body_rot = tuple(float(m.group(i)) for i in (5, 6, 7, 8))
                body_rot_at[cur_tick] = cur_body_rot
                continue
            m = SNAP_RE.search(line)
            if m and int(m.group(1)) == target_eid:
                # snapshot's rot is what the body will be reconciled to
                # eventually; treat it as the authoritative target curve.
                cur_body_rot = tuple(float(m.group(i)) for i in (2, 3, 4, 5))
                body_rot_at[cur_tick] = cur_body_rot
                continue
            m = FRAME_RE.search(line)
            if m and int(m.group(1)) == target_eid:
                tick = int(m.group(2))
                if tick_lo <= tick <= tick_hi:
                    q = tuple(float(m.group(i)) for i in (9, 10, 11, 12))
                    frames.append((tick, q))
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


def render_svg(before, after, eid, tick_lo, tick_hi, out_path):
    W, H = 1400, 760
    PADL, PADR, PADT, PADB = 70, 30, 50, 60
    plot_w = W - PADL - PADR
    plot_h = (H - PADT - PADB - 40) // 2
    panels = [
        ('BEFORE - visRot frozen during PVB (eid={} ticks {}-{})'.format(eid, tick_lo, tick_hi), before, PADT),
        ('AFTER  - visRot slerps toward body.rot during PVB', after, PADT + plot_h + 40),
    ]
    all_y = []
    for _, data, _ in panels:
        frames, body_rot_at, _, _ = data
        for tick, q in frames:
            all_y.append(quat_to_yaw_deg(q))
        for tick, q in body_rot_at.items():
            if tick_lo <= tick <= tick_hi:
                all_y.append(quat_to_yaw_deg(q))
    if not all_y:
        print('no data in tick range {}-{} for eid={}'.format(tick_lo, tick_hi, eid), file=sys.stderr)
        sys.exit(2)
    y_min, y_max = min(all_y), max(all_y)
    if y_max - y_min < 1:
        y_max = y_min + 1
    y_pad = (y_max - y_min) * 0.1
    y_min -= y_pad
    y_max += y_pad

    def xs(tick):
        return PADL + (tick - tick_lo) / (tick_hi - tick_lo) * plot_w

    def ys(yaw, panel_top):
        return panel_top + plot_h - (yaw - y_min) / (y_max - y_min) * plot_h

    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="{}" height="{}" font-family="monospace" font-size="12">'.format(W, H)]
    out.append('<rect width="{}" height="{}" fill="#fdfdfd"/>'.format(W, H))
    out.append('<text x="{}" y="22" font-size="16" text-anchor="middle" font-weight="bold">PVB rotation slerp fix - cube eid={}, S7-C3 ticks {}-{}</text>'.format(W // 2, eid, tick_lo, tick_hi))

    for title, data, top in panels:
        frames, body_rot_at, pvb_starts, pvb_ends = data
        out.append('<rect x="{}" y="{}" width="{}" height="{}" fill="white" stroke="#bbb"/>'.format(PADL, top, plot_w, plot_h))
        out.append('<text x="{}" y="{}" font-weight="bold">{}</text>'.format(PADL, top - 8, title))

        if pvb_starts and pvb_ends:
            s = min(pvb_starts)
            e = max(pvb_ends)
            out.append('<rect x="{:.1f}" y="{}" width="{:.1f}" height="{}" fill="#fff2cc" opacity="0.6"/>'.format(xs(s), top, xs(e) - xs(s), plot_h))
            out.append('<text x="{:.1f}" y="{}" font-size="11" fill="#a07000">PVB active</text>'.format(xs(s) + 4, top + 14))

        for i in range(6):
            y_val = y_min + (y_max - y_min) * i / 5
            y_px = top + plot_h - (y_val - y_min) / (y_max - y_min) * plot_h
            out.append('<line x1="{}" y1="{:.1f}" x2="{}" y2="{:.1f}" stroke="#eee"/>'.format(PADL, y_px, PADL + plot_w, y_px))
            out.append('<text x="{}" y="{:.1f}" text-anchor="end" fill="#666">{:+.1f}deg</text>'.format(PADL - 6, y_px + 3, y_val))

        for tick in range(tick_lo, tick_hi + 1, 5):
            x_px = xs(tick)
            out.append('<line x1="{:.1f}" y1="{}" x2="{:.1f}" y2="{}" stroke="#888"/>'.format(x_px, top + plot_h, x_px, top + plot_h + 4))
            if tick % 10 == 0:
                out.append('<text x="{:.1f}" y="{}" text-anchor="middle" fill="#666">{}</text>'.format(x_px, top + plot_h + 18, tick))

        body_pts = sorted([(t, q) for t, q in body_rot_at.items() if tick_lo <= t <= tick_hi])
        if body_pts:
            pts = [(xs(t), ys(quat_to_yaw_deg(q), top)) for t, q in body_pts]
            d = 'M ' + ' L '.join('{:.1f},{:.1f}'.format(x, y) for x, y in pts)
            out.append('<path d="{}" stroke="#c00" stroke-width="1.5" fill="none" stroke-dasharray="4 3"/>'.format(d))

        if frames:
            pts = [(xs(t), ys(quat_to_yaw_deg(q), top)) for t, q in frames]
            d = 'M ' + ' L '.join('{:.1f},{:.1f}'.format(x, y) for x, y in pts)
            out.append('<path d="{}" stroke="#06c" stroke-width="2" fill="none"/>'.format(d))
            for x, y in pts:
                out.append('<circle cx="{:.1f}" cy="{:.1f}" r="2" fill="#06c"/>'.format(x, y))

    ly = H - 28
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#06c" stroke-width="2"/>'.format(PADL, ly, PADL + 30, ly))
    out.append('<text x="{}" y="{}">visRot (rendered visual yaw)</text>'.format(PADL + 36, ly + 4))
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#c00" stroke-width="1.5" stroke-dasharray="4 3"/>'.format(PADL + 260, ly, PADL + 290, ly))
    out.append('<text x="{}" y="{}">body.rot (authoritative rigidbody yaw)</text>'.format(PADL + 296, ly + 4))
    out.append('<rect x="{}" y="{}" width="14" height="14" fill="#fff2cc" stroke="#bbb"/>'.format(PADL + 560, ly - 8))
    out.append('<text x="{}" y="{}">PVB window</text>'.format(PADL + 580, ly + 4))

    out.append('</svg>')
    with open(out_path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(out))
    print('wrote {}'.format(out_path))


def render_svg_split(before, b_eid, b_lo, b_hi, after, a_eid, a_lo, a_hi, out_path):
    W, H = 1400, 760
    PADL, PADR, PADT, PADB = 70, 30, 50, 60
    plot_w = W - PADL - PADR
    plot_h = (H - PADT - PADB - 40) // 2

    # collect yaw range per panel separately (different tick ranges -> different absolute yaws)
    def yaw_range(frames, body_rot_at, lo, hi):
        ys = []
        for tick, q in frames:
            ys.append(quat_to_yaw_deg(q))
        for tick, q in body_rot_at.items():
            if lo <= tick <= hi:
                ys.append(quat_to_yaw_deg(q))
        if not ys:
            return -180, 180
        y_min, y_max = min(ys), max(ys)
        if y_max - y_min < 1:
            y_max = y_min + 1
        pad = (y_max - y_min) * 0.1
        return y_min - pad, y_max + pad

    panels = [
        ('BEFORE - PVB freezes visRot (eid={} ticks {}-{})'.format(b_eid, b_lo, b_hi), before, b_lo, b_hi, PADT),
        ('AFTER  - PVB slerps visRot toward body.rot (eid={} ticks {}-{})'.format(a_eid, a_lo, a_hi), after, a_lo, a_hi, PADT + plot_h + 40),
    ]

    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="{}" height="{}" font-family="monospace" font-size="12">'.format(W, H)]
    out.append('<rect width="{}" height="{}" fill="#fdfdfd"/>'.format(W, H))
    out.append('<text x="{}" y="22" font-size="16" text-anchor="middle" font-weight="bold">PVB rotation slerp fix - S7-C3 cube kick + settle</text>'.format(W // 2))

    for title, data, lo, hi, top in panels:
        frames, body_rot_at, pvb_starts, pvb_ends = data
        y_min, y_max = yaw_range(frames, body_rot_at, lo, hi)

        def xs(tick):
            return PADL + (tick - lo) / (hi - lo) * plot_w

        def ys(yaw):
            return top + plot_h - (yaw - y_min) / (y_max - y_min) * plot_h

        out.append('<rect x="{}" y="{}" width="{}" height="{}" fill="white" stroke="#bbb"/>'.format(PADL, top, plot_w, plot_h))
        out.append('<text x="{}" y="{}" font-weight="bold">{}</text>'.format(PADL, top - 8, title))

        if pvb_starts and pvb_ends:
            s = min(pvb_starts); e = max(pvb_ends)
            out.append('<rect x="{:.1f}" y="{}" width="{:.1f}" height="{}" fill="#fff2cc" opacity="0.6"/>'.format(xs(s), top, xs(e) - xs(s), plot_h))
            out.append('<text x="{:.1f}" y="{}" font-size="11" fill="#a07000">PVB active</text>'.format(xs(s) + 4, top + 14))

        for i in range(6):
            y_val = y_min + (y_max - y_min) * i / 5
            y_px = top + plot_h - (y_val - y_min) / (y_max - y_min) * plot_h
            out.append('<line x1="{}" y1="{:.1f}" x2="{}" y2="{:.1f}" stroke="#eee"/>'.format(PADL, y_px, PADL + plot_w, y_px))
            out.append('<text x="{}" y="{:.1f}" text-anchor="end" fill="#666">{:+.1f}deg</text>'.format(PADL - 6, y_px + 3, y_val))

        step = max(5, (hi - lo) // 16)
        for tick in range(lo, hi + 1, step):
            x_px = xs(tick)
            out.append('<line x1="{:.1f}" y1="{}" x2="{:.1f}" y2="{}" stroke="#888"/>'.format(x_px, top + plot_h, x_px, top + plot_h + 4))
            out.append('<text x="{:.1f}" y="{}" text-anchor="middle" fill="#666">{}</text>'.format(x_px, top + plot_h + 18, tick))

        body_pts = sorted([(t, q) for t, q in body_rot_at.items() if lo <= t <= hi])
        if body_pts:
            pts = [(xs(t), ys(quat_to_yaw_deg(q))) for t, q in body_pts]
            d = 'M ' + ' L '.join('{:.1f},{:.1f}'.format(x, y) for x, y in pts)
            out.append('<path d="{}" stroke="#c00" stroke-width="1.5" fill="none" stroke-dasharray="4 3"/>'.format(d))

        if frames:
            pts = [(xs(t), ys(quat_to_yaw_deg(q))) for t, q in frames]
            d = 'M ' + ' L '.join('{:.1f},{:.1f}'.format(x, y) for x, y in pts)
            out.append('<path d="{}" stroke="#06c" stroke-width="2" fill="none"/>'.format(d))
            for x, y in pts:
                out.append('<circle cx="{:.1f}" cy="{:.1f}" r="2" fill="#06c"/>'.format(x, y))

    ly = H - 28
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#06c" stroke-width="2"/>'.format(PADL, ly, PADL + 30, ly))
    out.append('<text x="{}" y="{}">visRot yaw (rendered visual)</text>'.format(PADL + 36, ly + 4))
    out.append('<line x1="{}" y1="{}" x2="{}" y2="{}" stroke="#c00" stroke-width="1.5" stroke-dasharray="4 3"/>'.format(PADL + 260, ly, PADL + 290, ly))
    out.append('<text x="{}" y="{}">body.rot yaw (authoritative rigidbody)</text>'.format(PADL + 296, ly + 4))
    out.append('<rect x="{}" y="{}" width="14" height="14" fill="#fff2cc" stroke="#bbb"/>'.format(PADL + 580, ly - 8))
    out.append('<text x="{}" y="{}">PVB window</text>'.format(PADL + 600, ly + 4))

    out.append('</svg>')
    with open(out_path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(out))
    print('wrote {}'.format(out_path))
if __name__ == '__main__':
    # 7-arg form: before_log after_log eid tick_lo tick_hi out.svg  -> same range both
    # 9-arg form: before_log before_eid before_lo before_hi after_log after_eid after_lo after_hi out.svg
    if len(sys.argv) == 7:
        before_log, after_log, eid, lo, hi, out = sys.argv[1:7]
        eid = int(eid); lo = int(lo); hi = int(hi)
        before = parse(before_log, eid, lo, hi)
        after = parse(after_log, eid, lo, hi)
        render_svg(before, after, eid, lo, hi, out)
    elif len(sys.argv) == 10:
        before_log, b_eid, b_lo, b_hi, after_log, a_eid, a_lo, a_hi, out = sys.argv[1:10]
        b_eid = int(b_eid); b_lo = int(b_lo); b_hi = int(b_hi)
        a_eid = int(a_eid); a_lo = int(a_lo); a_hi = int(a_hi)
        before = parse(before_log, b_eid, b_lo, b_hi)
        after = parse(after_log, a_eid, a_lo, a_hi)
        render_svg_split(before, b_eid, b_lo, b_hi, after, a_eid, a_lo, a_hi, out)
    else:
        print('usage:', file=sys.stderr)
        print('  plot_pvb_rotation.py BEFORE_LOG AFTER_LOG EID LO HI OUT.svg', file=sys.stderr)
        print('  plot_pvb_rotation.py BEFORE_LOG B_EID B_LO B_HI AFTER_LOG A_EID A_LO A_HI OUT.svg', file=sys.stderr)
        sys.exit(1)
