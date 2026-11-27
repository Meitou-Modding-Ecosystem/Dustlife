"""F1 war rhythm, checked before code (Project Euler rule). A faction's war-weariness W rises with its losses, relative to its strength, and
fades; it drives offensive -> exhausted -> truce -> offensive. Claims to check, for loss rates a real war shows:
  1. an offensive lasts 8 to 30 days before exhaustion (a campaign or two, not a week, not a season);
  2. exhaustion + truce last 10 to 25 days (a breath, then war again);
  3. in 90 days every faction that fights has at least one full cycle and is at war again after it (the war never stalls for good);
  4. a faction that loses nothing never tires (no clockwork truces).
W += 1 / max(4, bands) per band lost; W halves every HW days. Exhausted at W >= HI; truce once W <= LO after >= MIN_EXH days exhausted;
truce lasts TRUCE days; then offensive again. Losses: Poisson, lam per day in offensive (scaled by how many bands it has), lam_def while not
attacking (defending only). Bands: start N, +recruits per day up to N."""
import math, random, statistics as st

HW, HI, LO, MIN_EXH, TRUCE = 7.0, 0.5, 0.2, 5, (7, 14)

def run(lam, N=20, days=90, seed=1, lam_def_share=0.15, recruit=0.5):
    rnd = random.Random(seed)
    W, bands, state, since, truce_end = 0.0, N, "off", 0, 0
    off_lens, rest_lens, cycles, at_war_after = [], [], 0, False
    off_start, rest_start = 0, None
    for d in range(days):
        rate = lam * bands / N if state == "off" else lam * lam_def_share * bands / N
        lost = sum(1 for _ in range(int(rate * 4) + 4) if rnd.random() < rate / (int(rate * 4) + 4))
        lost = min(lost, bands)
        for _ in range(lost):
            W += 1.0 / max(4, bands); bands -= 1
        W *= 0.5 ** (1.0 / HW)
        bands = min(N, bands + (1 if rnd.random() < recruit else 0))
        if state == "off" and W >= HI:
            off_lens.append(d - off_start); state, since, rest_start = "exh", d, d
        elif state == "exh" and W <= LO and d - since >= MIN_EXH:
            state, truce_end = "truce", d + rnd.randint(*TRUCE)
        elif state == "truce" and d >= truce_end:
            rest_lens.append(d - rest_start); state, off_start = "off", d; cycles += 1
    at_war_after = state == "off" and cycles > 0
    return off_lens, rest_lens, cycles, at_war_after

for lam in (0.0, 0.5, 1.0, 1.5, 2.0, 3.0):
    offs, rests, cyc, after = [], [], [], 0
    for s in range(400):
        o, r, c, a = run(lam, seed=s)
        offs += o; rests += r; cyc.append(c); after += a
    def med(x): return st.median(x) if x else float("nan")
    print(f"losses {lam:3.1f}/day at war: offensive median {med(offs):5.1f} d, exhausted+truce median {med(rests):5.1f} d, "
          f"full cycles in 90 d median {med(cyc):3.0f}, back at war after a cycle in {100*after/400:3.0f}% of runs")
