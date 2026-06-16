"""Analyse the visual smoothness of a cube being pushed by the player.

Three-panel SVG:
  1. Z-position trajectory (bodyRaw vs visRendered) — push direction
  2. Per-tick |velocity| from bodyRaw deltas — physical smoothness of the
     rigidbody motion (jerky = resim is yanking it)
  3. visRendered − bodyRaw position gap — how far the visual lags the body

PVB-active windows and TIER-SWITCH events are overlaid.

Usage:
    python plot_push_analysis.py LOG EID TICK_LO TICK_HI OUT.svg
"""
import re
import sys
import math

FRAME_RE = re.compile(
    r"\[SMOOTH-FRAME\] body=(\d+) pf=\d+ clientTick=(\d+) dt=([\d.]+) pif=([\d.]+) "
    r"bodyRaw=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"bodyRot=\(([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"visRendered=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"visRot=\(([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"targetPrev=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"targetCurr=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"posOffset=\(([-\d.]+),([-\d.]+),([-\d.]+)\) "
    r"vel=\(([-\d.]+),([-\d.]+),([-\d.]+)\)"
)
TICK_RE = re.compile(r"\[CLIENT-TICK\] tick=(\d+) dt")
PVB_S_RE = re.compile(r"\[SMOOTH-PVB-START\] body=(\d+)")
PVB_E_RE = re.compile(r"\[SMOOTH-PVB-END\] body=(\d+)")
TIER_RE = re.compile(r"\[TIER-SWITCH\] tick=(\d+) eid=(\d+) (\S+)")


def parse(path, eid, lo, hi):
    frames = []  # (tick, bodyRaw, visRendered, bodyVelFromLog)
    pvb_starts = []
    pvb_ends = []
    tier_switches = []  # (tick, from_to)
    cur_tick = 0
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            t = TICK_RE.search(line)
            if t:
                cur_tick = int(t.group(1))
                continue
            m = TIER_RE.search(line)
            if m and int(m.group(2)) == eid:
                tt = int(m.group(1))
                if lo <= tt <= hi:
                    tier_switches.append((tt, m.group(3)))
                continue
            m = PVB_S_RE.search(line)
            if m and int(m.group(1)) == eid and lo <= cur_tick <= hi:
                pvb_starts.append(cur_tick); continue
            m = PVB_E_RE.search(line)
            if m and int(m.group(1)) == eid and lo <= cur_tick <= hi:
                pvb_ends.append(cur_tick); continue
            m = FRAME_RE.search(line)
            if m and int(m.group(1)) == eid:
                tick = int(m.group(2))
                if lo <= tick <= hi:
                    # Group indices (regex was bumped when bodyRot was added
                    # to SMOOTH-FRAME but these indices were not updated;
                    # blue/visual line came out flat because it was plotting
                    # visRot.X (a quat component near 0) instead of visRendered.Z):
                    #   1: body name
                    #   2: clientTick
                    #   3: dt    4: pif
                    #   5,6,7:    bodyRaw X,Y,Z
                    #   8,9,10,11: bodyRot X,Y,Z,W
                    #   12,13,14: visRendered X,Y,Z      <- correct
                    #   15,16,17,18: visRot X,Y,Z,W
                    #   19,20,21: targetPrev X,Y,Z
                    #   22,23,24: targetCurr X,Y,Z
                    #   25,26,27: posOffset X,Y,Z
                    #   28,29,30: vel X,Y,Z
                    bx, by, bz = float(m.group(5)), float(m.group(6)), float(m.group(7))
                    vx, vy, vz = float(m.group(12)), float(m.group(13)), float(m.group(14))
                    velx = float(m.group(28)); vely = float(m.group(29)); velz = float(m.group(30))
                    dt = float(m.group(3))
                    frames.append((tick, (bx, by, bz), (vx, vy, vz), (velx, vely, velz), dt))
    return frames, pvb_starts, pvb_ends, tier_switches


def render(label, panels_data, out_path):
    """panels_data: list of (panel_label, parsed_result). Stacks panels."""
    W, H = 1500, 1100
    PADL, PADR, PADT, PADB = 80, 40, 60, 60
    npanels = 3 * len(panels_data)  # 3 sub-plots per scenario
    gap = 18
    plot_w = W - PADL - PADR
    plot_h = (H - PADT - PADB - gap * (npanels - 1)) // npanels

    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" font-family="monospace" font-size="11">']
    out.append(f'<rect width="{W}" height="{H}" fill="#fdfdfd"/>')
    out.append(f'<text x="{W//2}" y="22" font-size="15" text-anchor="middle" font-weight="bold">{label}</text>')

    i_panel = 0
    for scenario_label, parsed in panels_data:
        frames, pvb_starts, pvb_ends, tier_switches = parsed
        if not frames:
            continue
        ticks = [f[0] for f in frames]
        lo, hi = min(ticks), max(ticks)
        bz = [f[1][2] for f in frames]
        vz = [f[2][2] for f in frames]
        speed = [math.sqrt(f[3][0]**2 + f[3][1]**2 + f[3][2]**2) for f in frames]
        gap_3d = [math.sqrt((f[2][0]-f[1][0])**2 + (f[2][1]-f[1][1])**2 + (f[2][2]-f[1][2])**2) for f in frames]

        # Visual velocity = consecutive visRendered deltas / dt. dt is the
        # render-frame interval logged on each SMOOTH-FRAME (NOT the physics
        # tick rate — render runs at 30-60 fps depending on engine load).
        # Body velocity comes straight from the smoother's logged vel field
        # (= RigidBody3D.LinearVelocity at the moment _Process ran), which is
        # the rigidbody's actual physical velocity that frame. Comparing the
        # two reveals freeze-then-jump patterns: body.vel is smooth-ish but
        # vis.vel collapses to 0 on freeze frames and spikes on catch-up.
        vis_speed = [0.0]
        for i in range(1, len(frames)):
            dt = frames[i][4]
            if dt <= 0:
                vis_speed.append(0.0)
                continue
            dx = frames[i][2][0] - frames[i-1][2][0]
            dy = frames[i][2][1] - frames[i-1][2][1]
            dz = frames[i][2][2] - frames[i-1][2][2]
            vis_speed.append(math.sqrt(dx*dx + dy*dy + dz*dz) / dt)

        sub_panels = [
            ('Z position', 'm', [(ticks, bz, '#c00', 'dash', 'body.z'), (ticks, vz, '#06c', 'solid', 'vis.z')]),
            ('|velocity|', 'm/s', [
                (ticks, speed, '#080', 'solid', '|body.vel|'),
                (ticks, vis_speed, '#06c', 'solid', '|vis.vel|'),
            ]),
            ('|vis - body| position gap', 'm', [(ticks, gap_3d, '#a060c0', 'solid', '|vis - body|')]),
        ]
        for sub_label, units, series in sub_panels:
            top = PADT + i_panel * (plot_h + gap)
            i_panel += 1

            all_y = []
            for _,ys,_,_,_ in series:
                all_y.extend(ys)
            if not all_y: all_y = [0,1]
            y_min, y_max = min(all_y), max(all_y)
            if y_max - y_min < 1e-6: y_max = y_min + 1
            yp = (y_max-y_min)*0.1
            y_min -= yp; y_max += yp

            def xs(tick): return PADL + (tick-lo)/(hi-lo)*plot_w
            def ys(y): return top + plot_h - (y-y_min)/(y_max-y_min)*plot_h

            out.append(f'<rect x="{PADL}" y="{top}" width="{plot_w}" height="{plot_h}" fill="white" stroke="#bbb"/>')
            out.append(f'<text x="{PADL}" y="{top-4}" font-weight="bold">{scenario_label} - {sub_label} ({units})</text>')

            # PVB shading
            if pvb_starts and pvb_ends:
                # pair starts with subsequent ends naively
                events = sorted([(t,'S') for t in pvb_starts] + [(t,'E') for t in pvb_ends])
                active_from = None
                for t,k in events:
                    if k=='S' and active_from is None: active_from = t
                    elif k=='E' and active_from is not None:
                        out.append(f'<rect x="{xs(active_from):.1f}" y="{top}" width="{xs(t)-xs(active_from):.1f}" height="{plot_h}" fill="#fff2cc" opacity="0.5"/>')
                        active_from = None

            # tier switches as vertical lines
            for tt, kind in tier_switches:
                color = '#e80' if 'Resim' in kind and kind.endswith('Resim') else '#08e'
                # Actually let me parse the kind better: "Interpolate->Resim" or "Resim->Interpolate"
                if 'Interpolate->Resim' in kind:
                    color = '#e80'  # orange = upgrade
                else:
                    color = '#08e'  # blue = downgrade
                xt = xs(tt)
                out.append(f'<line x1="{xt:.1f}" y1="{top}" x2="{xt:.1f}" y2="{top+plot_h}" stroke="{color}" stroke-width="0.7" stroke-dasharray="2 2" opacity="0.5"/>')

            # gridlines
            for j in range(5):
                yv = y_min + (y_max-y_min)*j/4
                yp_px = ys(yv)
                out.append(f'<line x1="{PADL}" y1="{yp_px:.1f}" x2="{PADL+plot_w}" y2="{yp_px:.1f}" stroke="#eee"/>')
                out.append(f'<text x="{PADL-4}" y="{yp_px+3:.1f}" text-anchor="end" fill="#666">{yv:+.3f}</text>')

            # x ticks
            step = max(5, (hi-lo)//14)
            for tick in range(lo, hi+1, step):
                xp = xs(tick)
                out.append(f'<line x1="{xp:.1f}" y1="{top+plot_h}" x2="{xp:.1f}" y2="{top+plot_h+3}" stroke="#888"/>')
                out.append(f'<text x="{xp:.1f}" y="{top+plot_h+14}" text-anchor="middle" fill="#666" font-size="10">{tick}</text>')

            # data lines
            for xs_data, ys_data, color, style, leg in series:
                dash = ' stroke-dasharray="4 3"' if style == 'dash' else ''
                pts = [(xs(t), ys(y)) for t,y in zip(xs_data, ys_data)]
                d = 'M ' + ' L '.join(f'{x:.1f},{y:.1f}' for x,y in pts)
                out.append(f'<path d="{d}" stroke="{color}" stroke-width="1.6" fill="none"{dash}/>')

    out.append('</svg>')
    with open(out_path, 'w', encoding='utf-8') as f: f.write('\n'.join(out))
    print('wrote', out_path)


if __name__ == '__main__':
    # plot_push_analysis.py OUT.svg [LABEL LOG EID LO HI]...
    out_path = sys.argv[1]
    rest = sys.argv[2:]
    if len(rest) % 5 != 0:
        print('usage: plot_push_analysis.py OUT.svg [LABEL LOG EID LO HI]...', file=sys.stderr)
        sys.exit(1)
    panels = []
    for i in range(0, len(rest), 5):
        label = rest[i]
        log = rest[i+1]
        eid = int(rest[i+2]); lo = int(rest[i+3]); hi = int(rest[i+4])
        panels.append((label, parse(log, eid, lo, hi)))
    render('Player push: cube position, velocity, visual gap', panels, out_path)
