#!/usr/bin/env python3
"""Key levels + rule-based option trade ideas for the daily options dashboard.

Usage:
    python3 trade_ideas.py data/2026-11-03.json            # prints both HTML panels
    python3 trade_ideas.py data/2026-11-03.json --inject daily_option_dashboard.html

Input JSON:
{
  "date": "2026-11-03",
  "market": {"vix": 20.83, "bias": "NEUTRAL"},          # bias: RISK-ON | RISK-OFF | NEUTRAL
  "tickers": [
    {"t": "NVDA", "spot": 208.78, "prev_close": 202.06,
     "ema9": 211.68, "ema21": 216.91, "ema50": 218.83, "ema200": 205.77,
     "rsi": 33.3, "macd_hist": -1.83,
     "pp": 204.87, "r1": 208.83, "r2": 213.01, "s1": 200.69, "s2": 196.73,
     "iv": 0.535,                       # ATM IV of the target expiry (decimal)
     "event": false,                    # earnings/binary event before target expiry
     "liquid": true}                    # options tight enough to trade (OI, spread)
  ]
}

The rules are deliberately simple and fully written down in RULES below so every
day's output is reproducible. They are a screening aid, not advice.
"""
import datetime as dt
import html
import json
import math
import re
import sys

RULES = """
Key levels  : classic floor pivots from the prior session (PP, R1/R2, S1/S2),
              9/21/50/200 EMA, prior close, 1-day and 1-week 1σ expected move.
Trend score : +1/-1 each for spot vs 21 EMA, spot vs 50 EMA, spot vs 200 EMA,
              9 EMA vs 21 EMA, MACD histogram sign  -> range -5..+5.
Direction   : BULL if score >= +1 and spot >= PP; BEAR if score <= -1 and spot < PP;
              otherwise NEUTRAL.
Levels used : all of S2/S1/PP/R1/R2, prior close, 9/21/50/200 EMA, sorted around the
              live price -> nearest support / resistance (handles gaps past R2/S2). RSI > 75 blocks new bull entries, RSI < 25 blocks
              new bear entries (stretched).
IV regime   : LOW < 35%, NORMAL 35-70%, HIGH > 70% (absolute; no IV-rank feed).
Structure   : BULL + LOW/NORMAL IV  -> call debit spread  (long ~ATM, short at first resistance within +1w EM)
              BULL + HIGH IV        -> bull put credit spr (short below support and -1w EM, 1 strike wide)
              BEAR + LOW/NORMAL IV  -> put debit spread   (long ~ATM, short at first support within -1w EM)
              BEAR + HIGH IV        -> bear call credit spr (short above resistance and +1w EM, 1 strike wide)
              NEUTRAL + HIGH IV     -> iron condor, shorts at ±1w EM
              NEUTRAL otherwise     -> no trade; alert on break of nearest resistance / support
Event       : earnings/binary event before expiry -> debit spreads only, size 0.5%.
Market      : VIX > 25 halves size; RISK-OFF skips bull ideas below the 200 EMA;
              RISK-ON skips bear ideas above the 200 EMA.
Expiry      : first Friday >= 7 calendar days out (no <7 DTE without a catalyst).
Size        : 2% NAV max risk (1% if VIX > 25, 0.5% on events).
Stops       : bull -> daily close below the nearest support at least 1/3 of a 1-day EM away;
              bear -> daily close above the equivalent resistance.
              Spread: exit at -50% of debit / 2x credit received.
Targets     : take half at +50%, close at 75-80% of max value.
"""


def strike_step(px):
    if px < 25: return 0.5
    if px < 50: return 1.0
    if px < 200: return 2.5
    if px < 500: return 5.0
    return 10.0


def snap(x, step, mode="near"):
    f = {"near": round, "up": math.ceil, "down": math.floor}[mode]
    v = f(x / step) * step
    return float(f"{v:.2f}")


def fmt(x):
    if x is None: return "—"
    return f"{x:,.2f}"


def target_expiry(today):
    d = today + dt.timedelta(days=7)
    while d.weekday() != 4:  # Friday
        d += dt.timedelta(days=1)
    return d


def levels_around(x):
    """Nearest supports (below spot, nearest first) and resistances (above, nearest first)."""
    s = x["spot"]
    cand = [("S2", x["s2"]), ("S1", x["s1"]), ("PP", x["pp"]), ("R1", x["r1"]), ("R2", x["r2"]),
            ("PDC", x.get("prev_close")), ("9EMA", x["ema9"]), ("21EMA", x["ema21"]),
            ("50EMA", x["ema50"]), ("200EMA", x["ema200"])]
    cand = [(k, v) for k, v in cand if v]
    tol = s * 0.001
    sup = sorted([c for c in cand if c[1] < s - tol], key=lambda c: -c[1])
    res = sorted([c for c in cand if c[1] > s + tol], key=lambda c: c[1])
    return sup, res


def lv(c):
    return f"{c[0]} {fmt(c[1])}" if c else "—"


def analyse(x, market, today):
    s = x["spot"]
    em1 = s * x["iv"] * math.sqrt(1 / 252)
    emw = s * x["iv"] * math.sqrt(5 / 252)
    score = sum([
        1 if s > x["ema21"] else -1,
        1 if s > x["ema50"] else -1,
        1 if s > x["ema200"] else -1,
        1 if x["ema9"] > x["ema21"] else -1,
        1 if x["macd_hist"] > 0 else -1,
    ])
    if score >= 1 and s >= x["pp"]:
        direction = "BULL"
    elif score <= -1 and s < x["pp"]:
        direction = "BEAR"
    else:
        direction = "NEUTRAL"
    sup, res = levels_around(x)
    # skip a level hugging spot (< 1/3 of a 1-day move) for stops
    far_sup = next((c for c in sup if s - c[1] >= em1 / 3), sup[0] if sup else None)
    far_res = next((c for c in res if c[1] - s >= em1 / 3), res[0] if res else None)
    s0, r0 = (sup[0] if sup else None), (res[0] if res else None)

    blocked = None
    if direction == "BULL" and x["rsi"] > 75:
        blocked = f"RSI {x['rsi']:.0f} stretched — wait for pullback to {lv(s0)}"
    if direction == "BEAR" and x["rsi"] < 25:
        blocked = f"RSI {x['rsi']:.0f} oversold — wait for bounce to {lv(r0)}"
    bias = market.get("bias", "NEUTRAL").upper()
    if direction == "BULL" and bias == "RISK-OFF" and s < x["ema200"]:
        blocked = "RISK-OFF tape and below 200 EMA"
    if direction == "BEAR" and bias == "RISK-ON" and s > x["ema200"]:
        blocked = "RISK-ON tape and above 200 EMA"

    iv = x["iv"]
    ivr = "LOW" if iv < 0.35 else ("HIGH" if iv > 0.70 else "NORMAL")
    step = strike_step(s)
    exp = target_expiry(today).isoformat()
    size = 0.5 if x.get("event") else (1.0 if market.get("vix", 0) > 25 else 2.0)
    idea = {"t": x["t"], "dir": direction, "score": score, "ivr": ivr, "iv": iv,
            "exp": exp, "size": size, "em1": em1, "emw": emw}
    if blocked:
        idea.update(struct="No trade", legs="—", trigger=blocked, target="—", stop="—")
        return idea

    credit = ivr == "HIGH" and not x.get("event")
    if direction == "BULL":
        trig = f"Hold above {lv(s0)}" + (f"; add on break of {lv(r0)}" if r0 else "; no overhead level — trail")
        tgts = [c for c in res if c[1] <= s + emw][:2]
        tgt = " → ".join(lv(c) for c in tgts) if tgts else f"+1w EM {fmt(s + emw)}"
        stop = f"Close < {lv(far_sup)}" if far_sup else f"Close < {fmt(s - em1)}"
        if credit:
            short = snap(min(far_sup[1] if far_sup else s, s - emw), step, "down")
            idea.update(struct="Bull put credit spread", legs=f"Sell {fmt(short)}P / Buy {fmt(short - step)}P")
        else:
            long_k = snap(s, step)
            lvl = next((c[1] for c in res if c[1] >= long_k + step and c[1] <= s + emw), s + emw)
            short_k = max(snap(lvl, step, "up"), long_k + step)
            idea.update(struct="Call debit spread", legs=f"Buy {fmt(long_k)}C / Sell {fmt(short_k)}C")
    elif direction == "BEAR":
        trig = f"Stay below {lv(r0)}" + (f"; add on break of {lv(s0)}" if s0 else "; no support below — trail")
        tgts = [c for c in sup if c[1] >= s - emw][:2]
        tgt = " → ".join(lv(c) for c in tgts) if tgts else f"−1w EM {fmt(s - emw)}"
        stop = f"Close > {lv(far_res)}" if far_res else f"Close > {fmt(s + em1)}"
        if credit:
            short = snap(max(far_res[1] if far_res else s, s + emw), step, "up")
            idea.update(struct="Bear call credit spread", legs=f"Sell {fmt(short)}C / Buy {fmt(short + step)}C")
        else:
            long_k = snap(s, step)
            lvl = next((c[1] for c in sup if c[1] <= long_k - step and c[1] >= s - emw), s - emw)
            short_k = min(snap(lvl, step, "down"), long_k - step)
            idea.update(struct="Put debit spread", legs=f"Buy {fmt(long_k)}P / Sell {fmt(short_k)}P")
    else:
        if credit:
            cs = snap(s + emw, step, "up")
            ps = snap(s - emw, step, "down")
            idea.update(struct="Iron condor", legs=f"Sell {fmt(ps)}P / {fmt(cs)}C, wings {fmt(step)}",
                        trigger=f"Range {lv(s0)} – {lv(r0)} holds", target="50% of credit",
                        stop=f"Close beyond {fmt(ps)} / {fmt(cs)}")
            return idea
        idea.update(struct="No trade", legs="—",
                    trigger=f"Alert: long > {lv(r0)} · short < {lv(s0)}", target="—", stop="—")
        return idea

    if x.get("event"):
        idea["struct"] += " (event — defined risk only)"
    if not x.get("liquid", True):
        idea["legs"] += " · thin chain: limit at mid or trade stock"
    idea.update(trigger=trig, target=tgt, stop=stop)
    return idea


PILL = {"BULL": ("pg", "🟢 BULL"), "BEAR": ("pr", "🔴 BEAR"), "NEUTRAL": ("po", "🟠 NEUTRAL")}


def key_levels_panel(rows, date):
    h = ['<h2>Key Levels <span class="note">(classic pivots from prior session · EMAs as of prior close)</span></h2>',
         '<div class="tw"><table>',
         '<tr><th>Ticker</th><th class="num">Spot</th><th class="num">Prev close</th><th class="num">S2</th><th class="num">S1</th>'
         '<th class="num">PP</th><th class="num">R1</th><th class="num">R2</th><th class="num">9 EMA</th><th class="num">21 EMA</th>'
         '<th class="num">50 EMA</th><th class="num">200 EMA</th><th class="num">1d EM ±</th><th class="num">1w 1σ range</th><th>vs PP</th></tr>']
    for x in rows:
        s = x["spot"]
        em1 = s * x["iv"] * math.sqrt(1 / 252)
        emw = s * x["iv"] * math.sqrt(5 / 252)
        side = '<span class="g">above</span>' if s >= x["pp"] else '<span class="r">below</span>'
        h.append(
            f'<tr><td><b>{html.escape(x["t"])}</b></td><td class="num"><b>{fmt(s)}</b></td><td class="num">{fmt(x.get("prev_close"))}</td>'
            f'<td class="num">{fmt(x["s2"])}</td><td class="num"><b>{fmt(x["s1"])}</b></td><td class="num"><b>{fmt(x["pp"])}</b></td>'
            f'<td class="num"><b>{fmt(x["r1"])}</b></td><td class="num">{fmt(x["r2"])}</td><td class="num">{fmt(x["ema9"])}</td>'
            f'<td class="num">{fmt(x["ema21"])}</td><td class="num">{fmt(x["ema50"])}</td><td class="num">{fmt(x["ema200"])}</td>'
            f'<td class="num">{fmt(em1)}</td><td class="num">{fmt(s - emw)} – {fmt(s + emw)}</td><td>{side}</td></tr>')
    h.append('</table></div>')
    h.append('<p class="note">PP = (H+L+C)/3 of prior session; R1 = 2·PP−L; S1 = 2·PP−H; R2 = PP+(H−L); S2 = PP−(H−L). '
             'On a large gap, price can open beyond R2/S2 — then the EMAs and 1σ band are the next reference levels.</p>')
    return "\n".join(h)


def ideas_panel(ideas, market):
    order = {"BULL": 0, "BEAR": 1, "NEUTRAL": 2}
    ideas = sorted(ideas, key=lambda i: (i["struct"] == "No trade", order[i["dir"]], -abs(i["score"])))
    h = ['<h2>Trade Ideas <span class="note">(rule-based from key levels + market regime)</span></h2>',
         f'<div class="bias"><b>Regime:</b> VIX {market.get("vix")} · bias {html.escape(market.get("bias", "NEUTRAL"))} · '
         f'target expiry <b>{ideas[0]["exp"] if ideas else "—"}</b> (first Friday ≥ 7 DTE). '
         'Strikes are rounded to standard increments — confirm they are listed and price at the mid before entering.</div>',
         '<div class="tw" style="margin-top:12px"><table>',
         '<tr><th>Ticker</th><th>Setup</th><th class="num">Trend</th><th>IV</th><th>Structure</th><th>Legs</th>'
         '<th>Entry trigger</th><th>Target</th><th>Stop</th><th class="num">Max risk</th></tr>']
    for i in ideas:
        cls, lab = PILL[i["dir"]]
        h.append(
            f'<tr><td><b>{html.escape(i["t"])}</b></td><td><span class="pill {cls}">{lab}</span></td>'
            f'<td class="num">{i["score"]:+d}</td><td>{i["ivr"]} {i["iv"]*100:.0f}%</td><td>{html.escape(i["struct"])}</td>'
            f'<td class="w">{html.escape(i["legs"])}</td><td class="w">{html.escape(i["trigger"])}</td>'
            f'<td>{html.escape(i["target"])}</td><td>{html.escape(i["stop"])}</td>'
            f'<td class="num">{"—" if i["struct"] == "No trade" else str(i["size"]) + "% NAV"}</td></tr>')
    h.append('</table></div>')
    h.append('<details class="note" style="margin-top:10px"><summary>Rules used</summary><pre style="white-space:pre-wrap">'
             + html.escape(RULES.strip()) + '</pre></details>')
    return "\n".join(h)


def inject(path, levels_html, ideas_html):
    """Insert/replace the two panels and their tabs in the dashboard HTML."""
    s = open(path).read()
    for pid in ("levels", "ideas"):
        s = re.sub(rf'<section class="panel" id="panel-{pid}".*?</section>\n', "", s, flags=re.S)
        s = re.sub(rf'<button role="tab" id="t{pid}".*?</button>\n', "", s, flags=re.S)
    panels = (f'<section class="panel" id="panel-levels" role="tabpanel" aria-labelledby="tlevels">\n{levels_html}\n</section>\n'
              f'<section class="panel" id="panel-ideas" role="tabpanel" aria-labelledby="tideas">\n{ideas_html}\n</section>\n')
    marker = '<section class="panel" id="panel-s1"'
    s = s.replace(marker, panels + marker, 1)
    tabs = ('<button role="tab" id="tlevels" data-tab="levels" aria-controls="panel-levels"><span class="tn">★</span>Key Levels</button>\n'
            '<button role="tab" id="tideas" data-tab="ideas" aria-controls="panel-ideas"><span class="tn">★</span>Trade Ideas</button>\n')
    s = s.replace('<button role="tab" id="t1"', tabs + '<button role="tab" id="t1"', 1)
    open(path, "w").write(s)


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    data = json.load(open(sys.argv[1]))
    today = dt.date.fromisoformat(data["date"])
    rows = data["tickers"]
    ideas = [analyse(x, data["market"], today) for x in rows]
    lv, ti = key_levels_panel(rows, data["date"]), ideas_panel(ideas, data["market"])
    if "--inject" in sys.argv:
        inject(sys.argv[sys.argv.index("--inject") + 1], lv, ti)
        print(f"injected {len(rows)} tickers, {sum(i['struct'] != 'No trade' for i in ideas)} ideas")
    else:
        print(lv + "\n\n" + ti)


if __name__ == "__main__":
    main()
