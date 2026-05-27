import re, sys, os, collections, statistics, glob

# Pick the 6 most recent run folders (3 + 3 confirmation)
runs = sorted(glob.glob("tests/TestResults/Quantitative/*"), key=os.path.getmtime, reverse=True)[:6]

def stats(values):
    if not values: return "n/a"
    return f"min={min(values)} max={max(values)} median={int(statistics.median(values))} mean={statistics.mean(values):.2f}"

def histogram(values):
    if not values: return ""
    c = collections.Counter(values)
    return " ".join(f"{k}:{c[k]}" for k in sorted(c.keys()))

# Aggregate per-condition across the 6 runs
agg = {c: {"finals": [], "maxes": [], "all_samples": [], "ups": [], "downs": []} for c in ["C0","C1","C2","C3","C4"]}

for run_dir in runs:
    print(f"=== {os.path.basename(run_dir)} ===")
    for cond in ["C0", "C1", "C2", "C3", "C4"]:
        log_path = os.path.join(run_dir, f"S7-MultiBodyChaos.{cond}.client.log")
        if not os.path.exists(log_path):
            continue
        values = []
        ups = downs = 0
        with open(log_path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                m = re.search(r"\[INPUT-DELAY-FEEDBACK\].*inputDelay=(\d+)", line)
                if m: values.append(int(m.group(1)))
                if "[INPUT-DELAY-ADJUST] bumped UP" in line: ups += 1
                elif "[INPUT-DELAY-ADJUST] bumped DOWN" in line: downs += 1
        if not values: continue
        final = values[-1]
        agg[cond]["finals"].append(final)
        agg[cond]["maxes"].append(max(values))
        agg[cond]["all_samples"].extend(values)
        agg[cond]["ups"].append(ups)
        agg[cond]["downs"].append(downs)
        print(f"  {cond}: final={final}  UP={ups:3d}  DOWN={downs:3d}  {stats(values)}")
    print()

print("=" * 70)
print("AGGREGATE across 6 runs")
print("=" * 70)
for cond in ["C0","C1","C2","C3","C4"]:
    a = agg[cond]
    if not a["finals"]: continue
    print(f"{cond}: finals={a['finals']}  maxes={a['maxes']}")
    print(f"      all-sample distribution (across all 6 runs):")
    print(f"         {stats(a['all_samples'])}")
    print(f"         histogram: {histogram(a['all_samples'])}")
    pct_2 = 100 * a["all_samples"].count(2) / len(a["all_samples"])
    print(f"         % of samples at default(2): {pct_2:.1f}%")
    pct_final_at_2 = 100 * a["finals"].count(2) / len(a["finals"])
    print(f"         runs ending at 2: {a['finals'].count(2)}/{len(a['finals'])} ({pct_final_at_2:.0f}%)")
    print()
