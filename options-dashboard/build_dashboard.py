#!/usr/bin/env python3
"""Render the full Daily Options Dashboard from one day's data file.

Usage:
    python3 build_dashboard.py data/YYYY-MM-DD.json [-o daily_option_dashboard.html]

Everything numeric (technical stack, Greeks, expected-move bands, support/resistance,
key levels, rule-based trade ideas) is computed here from the data file, so every day's
page has the same structure. Narrative sections (macro, deep dive, top plays, avoid,
risk, calendar, rules, sources) are taken from the data file as text rows.

Text fields accept **bold** markup; everything else is HTML-escaped.

Data file = the trade_ideas.py schema plus these optional keys:
  title, subtitle, asof
  per ticker: group ("core"|"bull"|"bear"), chg_pct, vol_m, catalyst, rating, macd,
              macd_signal, stars (override), sent (override "bull"|"bear"|"caution"),
              pattern, opt {strike,type,exp,delta,gamma,theta,vega,iv,mark,oi}
  macro: {rows: [[asset, level, chg, read, sent]], events: [[when, text]], bias: text}
  em_notes: [[topic, text]]
  deep_dive: [{title, sent, rows: [[label, text]]}], deep_dive_note: text
  top_plays: [{sent, title, paras: [text]}]
  avoid, risk, calendar, rules: [[col, col, ...]]  (first header row is fixed per section)
  unverified: text, sources: [[label, url]]
"""
import argparse
import html
import json
import math
import os
import re
import sys
import datetime as dt

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import trade_ideas as TI  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
SENT = {"bull": ("pg", "🟢"), "bear": ("pr", "🔴"), "caution": ("po", "🟠")}
DIR2SENT = {"BULL": "bull", "BEAR": "bear", "NEUTRAL": "caution"}


def md(text):
    """Escape, then allow **bold**."""
    t = html.escape(str(text))
    return re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", t)


def pill(sent, label=None):
    cls, emo = SENT.get(sent, SENT["caution"])
    return f'<span class="pill {cls}">{emo}{(" " + md(label)) if label else ""}</span>'


def f2(x):
    return "—" if x is None else f"{x:,.2f}"


def table(header, rows, wide=()):
    """rows are lists of already-rendered HTML cells."""
    h = ['<div class="tw"><table>', "<tr>" + "".join(f"<th>{c}</th>" for c in header) + "</tr>"]
    for r in rows:
        h.append("<tr>" + "".join(
            f'<td class="w">{c}</td>' if i in wide else f"<td>{c}</td>" for i, c in enumerate(r)) + "</tr>")
    h.append("</table></div>")
    return "\n".join(h)


def text_table(header, rows, wide=()):
    return table(header, [[md(c) for c in r] for r in rows], wide)


def stack(x):
    s, a, b, c, d = x["spot"], x["ema9"], x["ema21"], x["ema50"], x["ema200"]
    if s > a > b > c > d:
        return pill("bull", "BULL")
    if s < a < b < c < d:
        return pill("bear", "BEAR")
    flags = []
    if s < d: flags.append("&lt;200")
    if s < c: flags.append("&lt;50")
    if s < b: flags.append("&lt;21")
    return pill("caution", "MIXED") + (" " + " ".join(flags) if flags else " px above all" if s > max(a, b, c, d) else "")


def stars(n):
    n = max(1, min(5, n))
    return "★" * n + "☆" * (5 - n)


def enrich(x, idea):
    s = x["spot"]
    x["_em1"] = s * x["iv"] * math.sqrt(1 / 252)
    x["_emw"] = s * x["iv"] * math.sqrt(5 / 252)
    sup, res = TI.levels_around(x)
    x["_sup"], x["_res"] = sup, res
    x["_idea"] = idea
    x["_sent"] = x.get("sent") or DIR2SENT[idea["dir"]]
    if "stars" not in x:
        sc = abs(idea["score"])
        st = 4 if sc >= 5 else 3 if sc >= 3 else 2
        if idea["struct"] == "No trade" or not x.get("liquid", True):
            st = min(st, 2)
        x["stars"] = st
    return x


def universe_table(rows):
    hdr = ["Ticker", "Sent.", "Price", "%Chg", "Vol", "Catalyst", "Rating / mean PT", "Support", "Resistance",
           "EM ±$ (1d)", "IV %", "Conv."]
    out = []
    for x in sorted(rows, key=lambda r: (-r["stars"], -abs(r.get("chg_pct", 0)))):
        chg = x.get("chg_pct")
        chg_html = "—" if chg is None else f'<span class="{"g" if chg >= 0 else "r"}">{chg:+.2f}%</span>'
        sup = " / ".join(f"{k} <b>{f2(v)}</b>" for k, v in x["_sup"][:2]) or "—"
        res = " / ".join(f"{k} <b>{f2(v)}</b>" for k, v in x["_res"][:2]) or "—"
        out.append([f"<b>{md(x['t'])}</b>", pill(x["_sent"]), f"<b>{f2(x['spot'])}</b>", chg_html,
                    f"{x['vol_m']:.1f}M" if x.get("vol_m") else "⚠️", md(x.get("catalyst", "")),
                    md(x.get("rating", "⚠️")), sup, res, f2(x["_em1"]), f"{x['iv']*100:.1f}", stars(x["stars"])])
    return table(hdr, out, wide=(5, 6, 7, 8))


def technicals_table(rows):
    hdr = ["Ticker", "Price", "9 EMA", "21 EMA", "50 EMA", "200 EMA", "Stack", "RSI", "MACD (hist)", "Pattern / flags"]
    out = []
    for x in sorted(rows, key=lambda r: -r["_idea"]["score"]):
        hst = x["macd_hist"]
        macd = f'<span class="{"g" if hst > 0 else "r"}">{"Bull" if hst > 0 else "Bear"} {hst:+.2f}</span>'
        rsi = f"<b>{x['rsi']:.1f}</b>" if x["rsi"] >= 70 or x["rsi"] <= 30 else f"{x['rsi']:.1f}"
        pat = x.get("pattern") or ("Overbought" if x["rsi"] >= 70 else "Oversold" if x["rsi"] <= 30 else "")
        out.append([f"<b>{md(x['t'])}</b>", f2(x["spot"]), f2(x["ema9"]), f2(x["ema21"]), f2(x["ema50"]),
                    f2(x["ema200"]), stack(x), rsi, macd, md(pat)])
    return table(hdr, out)


def greeks_table(rows):
    hdr = ["Ticker", "Spot", "ATM strike / exp", "Type", "Δ", "Γ", "Θ/day", "ν", "IV %", "1d EM ±%", "1w EM ±$"]
    out = []
    for x in rows:
        o = x.get("opt") or {}
        out.append([f"<b>{md(x['t'])}</b>", f2(x["spot"]),
                    f"{f2(o.get('strike'))} / {o.get('exp', '—')[5:]}" if o else "⚠️",
                    o.get("type", "—"), f"{o.get('delta', 0):.3f}" if o else "—", f"{o.get('gamma', 0):.4f}" if o else "—",
                    f"{o.get('theta', 0):.2f}" if o else "—", f"{o.get('vega', 0):.3f}" if o else "—",
                    f"{x['iv']*100:.1f}", f"{x['_em1']/x['spot']*100:.1f}%", f2(x["_emw"])])
    return table(hdr, out)


def bands_table(rows):
    hdr = ["Ticker", "Lower 2σ", "Lower 1σ", "Spot", "Upper 1σ", "Upper 2σ"]
    out = [[f"<b>{md(x['t'])}</b>", f2(x["spot"] - 2 * x["_emw"]), f"<b>{f2(x['spot'] - x['_emw'])}</b>",
            f2(x["spot"]), f"<b>{f2(x['spot'] + x['_emw'])}</b>", f2(x["spot"] + 2 * x["_emw"])] for x in rows]
    return table(hdr, out)


def section(pid, html_body):
    return (f'<section class="panel" id="panel-{pid}" role="tabpanel" aria-labelledby="t{pid}">\n'
            f"{html_body}\n</section>")


def build(data):
    today = dt.date.fromisoformat(data["date"])
    rows = [TI.fill_pivots(x) for x in data["tickers"]]
    ideas = [TI.analyse(x, data["market"], today) for x in rows]
    rows = [enrich(x, i) for x, i in zip(rows, ideas)]
    groups = {g: [x for x in rows if x.get("group", "core") == g] for g in ("core", "bull", "bear")}

    panels = []
    panels.append(section("levels", TI.key_levels_panel(rows, data["date"])))
    panels.append(section("ideas", TI.ideas_panel(ideas, data["market"])))

    m = data.get("macro", {})
    p1 = ['<h2>1 · Macro Pulse</h2>',
          table(["Asset", "Level", "Chg", "Read"],
                [[md(a), f"<b>{md(l)}</b>", md(c), f"{pill(snt)} {md(rd)}"] for a, l, c, rd, snt in m.get("rows", [])],
                wide=(3,))]
    if m.get("events"):
        p1 += ["<h3>Data &amp; overnight</h3>", text_table(["When", "Event"], m["events"], wide=(1,))]
    if m.get("bias"):
        p1.append(f'<div class="bias">{md(m["bias"])}</div>')
    panels.append(section("s1", "\n".join(p1)))

    p2 = ["<h2>2 · Universe Main Grid</h2>",
          "<h3>A + B · Mag 7 + watchlist</h3>", universe_table(groups["core"])]
    if groups["bull"]:
        p2 += [f"<h3>C · Top bullish ({len(groups['bull'])})</h3>", universe_table(groups["bull"])]
    if groups["bear"]:
        p2 += [f"<h3>D · Top bearish ({len(groups['bear'])})</h3>", universe_table(groups["bear"])]
    p2.append('<p class="note">Support/resistance = nearest levels around spot from pivots, prior close and 9/21/50/200 EMA. '
              "Sentiment and stars default to the Trade Ideas engine (trend score, setup, liquidity).</p>")
    panels.append(section("s2", "\n".join(p2)))

    panels.append(section("s3", "<h2>3 · EMA / Technical Stack</h2>\n" + technicals_table(rows)))

    p4 = ["<h2>4 · Greeks &amp; Expected Move</h2>",
          '<p class="note">ATM option marks. Θ and ν per share (ν per 1 IV pt). EM = spot × IV × √(days/252); 1w = 5 sessions.</p>',
          greeks_table(rows), "<h3>Expected-move bands (1 week)</h3>", bands_table(rows)]
    if data.get("em_notes"):
        p4 += ["<h3>What the moves mean</h3>", text_table(["Topic", "Read"], data["em_notes"], wide=(1,))]
    panels.append(section("s4", "\n".join(p4)))

    p5 = ["<h2>5 · Deep Dive</h2>"]
    for d in data.get("deep_dive", []):
        p5 += [f"<h3>{pill(d.get('sent', 'caution'))} {md(d['title'])}</h3>",
               text_table(["", ""], d["rows"], wide=(1,))]
    if data.get("deep_dive_note"):
        p5.append(f'<div class="warn">{md(data["deep_dive_note"])}</div>')
    panels.append(section("s5", "\n".join(p5)))

    cards = []
    for p in data.get("top_plays", []):
        paras = "".join(f"<p>{md(t)}</p>" for t in p["paras"])
        cards.append(f'<div class="card"><h4>{pill(p.get("sent", "caution"))} {md(p["title"])}</h4>{paras}</div>')
    panels.append(section("s6", '<h2>6 · Top 3 Plays</h2>\n<div class="cards">' + "".join(cards) + "</div>"))

    panels.append(section("s7", "<h2>7 · Avoid List</h2>\n" +
                          text_table(["Ticker / sector", "Reason", "Detail"], data.get("avoid", []), wide=(2,))))
    panels.append(section("s8", "<h2>8 · Risk Dashboard</h2>\n" +
                          text_table(["Metric", "Reading", "Signal"], data.get("risk", []), wide=(1,))))
    panels.append(section("s9", "<h2>9 · Week-Ahead Calendar</h2>\n" +
                          text_table(["Day", "Time", "Ticker / event", "What to watch"], data.get("calendar", []), wide=(3,))))
    panels.append(section("s10", "<h2>10 · Risk Management Rules</h2>\n" +
                          text_table(["Rule", "Spec"], data.get("rules", []), wide=(1,))))

    labels = [("levels", "★", "Key Levels"), ("ideas", "★", "Trade Ideas"), ("s1", "1", "Macro"), ("s2", "2", "Universe"),
              ("s3", "3", "Technicals"), ("s4", "4", "Greeks &amp; EM"), ("s5", "5", "Deep Dive"), ("s6", "6", "Top 3"),
              ("s7", "7", "Avoid"), ("s8", "8", "Risk"), ("s9", "9", "Calendar"), ("s10", "10", "Rules")]
    tabs = ('<div class="tabs" role="tablist">\n' + "".join(
        f'<button role="tab" id="t{i}" data-tab="{i}" aria-controls="panel-{i}"><span class="tn">{n}</span>{lab}</button>\n'
        for i, n, lab in labels) + '<button role="tab" id="tall" data-tab="all">All</button>\n</div>')

    foot = []
    if data.get("unverified"):
        foot.append(f'<div class="warn"><b>⚠️ Not verified:</b> {md(data["unverified"])}</div>')
    if data.get("sources"):
        foot.append('<p class="note"><b>Sources:</b> ' + " · ".join(
            f'<a href="{html.escape(u, quote=True)}">{md(lab)}</a>' for lab, u in data["sources"]) + "</p>")
    foot.append('<div class="disc"><b>Disclaimer:</b> Educational analysis only — not investment advice. Options involve '
                "substantial risk, including total loss of premium. Verify every price, Greek and level against your own "
                "broker feed before trading.</div>")

    tpl = open(os.path.join(HERE, "template.html")).read()
    title = data.get("title") or f"Daily Options Dashboard — {today.strftime('%a %b %-d, %Y')}"
    return (tpl.replace("{{TITLE}}", md(title)).replace("{{SUBTITLE}}", md(data.get("subtitle", "")))
            .replace("{{TABS}}", tabs).replace("{{PANELS}}", "\n".join(panels)).replace("{{FOOTER}}", "\n".join(foot)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("data")
    ap.add_argument("-o", "--out", default=os.path.join(HERE, "daily_option_dashboard.html"))
    a = ap.parse_args()
    data = json.load(open(a.data))
    out = build(data)
    open(a.out, "w").write(out)
    print(f"wrote {a.out} ({len(out):,} bytes, {len(data['tickers'])} tickers)")


if __name__ == "__main__":
    main()
