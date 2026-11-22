"""Сводит профили docs/profiles/*.json|csv в таблицы (docs/profiles/summary.md) и SVG-графики (docs/graphs/).

    python tools/make_report.py

Без внешних зависимостей: графики пишутся как SVG вручную.
"""
import csv
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILES = os.path.join(ROOT, "docs", "profiles")
GRAPHS = os.path.join(ROOT, "docs", "graphs")

MODES = ["baseline", "grid", "dormancy", "push", "quantize", "frequency", "all", "everything"]
MODE_NAMES = {
    "baseline": "Baseline (всё, всем, 50 Гц, float)",
    "grid": "Replication Graph (сетка + дистанция)",
    "dormancy": "Net Dormancy",
    "push": "Push-Based (только грязные поля)",
    "quantize": "Квантование",
    "frequency": "NetUpdateFrequency по классам",
    "all": "Все обязательные вместе",
    "everything": "Все + доп. (NetPriority, IsNetRelevantFor)",
}
KINDS = ["tree", "crate", "pickup", "bot", "projectile"]
KIND_NAMES = {"tree": "Деревья", "crate": "Ящики", "pickup": "Аптечки", "bot": "Боты", "projectile": "Снаряды"}
KIND_COLORS = {"tree": "#3f8f4a", "crate": "#c08a45", "pickup": "#d24a55", "bot": "#8a55cc", "projectile": "#e0b52a"}


def load(mode, role="server"):
    path = os.path.join(PROFILES, f"netprofile_{mode}_{role}.json")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def load_series(mode, column, role="server"):
    path = os.path.join(PROFILES, f"netprofile_{mode}_{role}.csv")
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8") as f:
        return [(float(r["t"]), float(r[column])) for r in csv.DictReader(f)]


def client_avg(mode, key):
    vals = []
    for i in range(1, 9):
        d = load(mode, f"client{i}")
        if d:
            vals.append(d[key])
    return sum(vals) / len(vals) if vals else 0.0


def fmt(v, digits=1):
    return f"{v:,.{digits}f}".replace(",", " ")


def pct(after, before):
    return f"−{(1 - after / before) * 100:.1f} %" if before else "—"


def bar_chart(data, title, path, unit="КБ/с"):
    """Горизонтальные столбцы, каждый разбит по классам акторов + «прочее»."""
    w, row, left, top = 860, 34, 330, 50
    h = top + row * len(data) + 75
    vmax = max(sum(parts.values()) for _, parts in data) or 1
    scale = (w - left - 90) / vmax
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" font-family="Segoe UI, Arial" font-size="13">',
           f'<rect width="{w}" height="{h}" fill="#ffffff"/>',
           f'<text x="{w / 2}" y="26" text-anchor="middle" font-size="16" font-weight="600">{title}</text>']
    for i, (label, parts) in enumerate(data):
        y = top + i * row
        out.append(f'<text x="{left - 8}" y="{y + 20}" text-anchor="end">{label}</text>')
        x = left
        for key, val in parts.items():
            width = val * scale
            color = KIND_COLORS.get(key, "#9aa3ad")
            if width > 0.3:
                out.append(f'<rect x="{x:.1f}" y="{y + 6}" width="{width:.1f}" height="{row - 12}" fill="{color}"/>')
            x += width
        out.append(f'<text x="{x + 6:.1f}" y="{y + 20}">{fmt(sum(parts.values()))} {unit}</text>')
    ly = top + row * len(data) + 25
    lx = 40
    for key in KINDS + ["other"]:
        name = KIND_NAMES.get(key, "Прочее (движение игроков, заголовки Netcode)")
        if key == "other":
            lx, ly = 40, ly + 22
        out.append(f'<rect x="{lx}" y="{ly - 11}" width="12" height="12" fill="{KIND_COLORS.get(key, "#9aa3ad")}"/>')
        out.append(f'<text x="{lx + 16}" y="{ly}">{name}</text>')
        lx += 16 + 8 * len(name) + 18
    out.append("</svg>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out))


def line_chart(series, title, path, ylabel):
    w, h, left, right, top, bottom = 860, 410, 70, 20, 45, 95
    pts = [p for _, s, _ in series for p in s]
    if not pts:
        return
    xmax = max(t for t, _ in pts) or 1
    ymax = max(v for _, v in pts) * 1.1 or 1
    sx = (w - left - right) / xmax
    sy = (h - top - bottom) / ymax
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" font-family="Segoe UI, Arial" font-size="13">',
           f'<rect width="{w}" height="{h}" fill="#ffffff"/>',
           f'<text x="{w / 2}" y="24" text-anchor="middle" font-size="16" font-weight="600">{title}</text>']
    for k in range(6):
        v = ymax * k / 5
        y = h - bottom - v * sy
        out.append(f'<line x1="{left}" y1="{y:.1f}" x2="{w - right}" y2="{y:.1f}" stroke="#e3e6ea"/>')
        out.append(f'<text x="{left - 6}" y="{y + 4:.1f}" text-anchor="end">{fmt(v, 0)}</text>')
    for t in range(0, int(xmax) + 1, 10):
        x = left + t * sx
        out.append(f'<text x="{x:.1f}" y="{h - bottom + 18}" text-anchor="middle">{t}</text>')
    out.append(f'<text x="{(w + left) / 2}" y="{h - bottom + 38}" text-anchor="middle">время записи, с</text>')
    out.append(f'<text x="16" y="{(h - bottom + top) / 2}" transform="rotate(-90 16 {(h - bottom + top) / 2})" text-anchor="middle">{ylabel}</text>')
    for label, s, color in series:
        d = " ".join(f"{left + t * sx:.1f},{h - bottom - v * sy:.1f}" for t, v in s)
        out.append(f'<polyline points="{d}" fill="none" stroke="{color}" stroke-width="2"/>')
    for i, (label, _, color) in enumerate(series):
        lx = left + (i % 2) * 380
        ly = h - 30 + (i // 2) * 20
        out.append(f'<rect x="{lx}" y="{ly - 6}" width="14" height="4" fill="{color}"/>')
        out.append(f'<text x="{lx + 20}" y="{ly}">{label}</text>')
    out.append("</svg>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out))


def main():
    import sys
    sys.stdout.reconfigure(encoding="utf-8")
    os.makedirs(GRAPHS, exist_ok=True)
    runs = {m: load(m) for m in MODES}
    runs = {m: d for m, d in runs.items() if d}
    base = runs.get("baseline")
    lines = []

    lines.append("## Итог по режимам (сервер, среднее за время записи)\n")
    lines.append("| Режим | Исходящий трафик сервера, КБ/с | Изменение | Репликация акторов, КБ/с | Отправок транспорту/с | Записей/с | Релевантно акторов на клиента | Акторов обработано/с | Кадр репликации, мс |")
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for m, d in runs.items():
        lines.append(f"| {MODE_NAMES[m]} | {fmt(d['total_kbps'])} | {pct(d['total_kbps'], base['total_kbps']) if base and m != 'baseline' else '—'} "
                     f"| {fmt(d['rep_kbps'])} | {fmt(d['sends_per_s'], 0)} | {fmt(d['records_per_s'], 0)} | {fmt(d['relevant_per_client'])} "
                     f"| {fmt(d['considered_per_s'], 0)} | {d['rep_ms_per_tick']:.3f} |")

    lines.append("\n## Трафик по классам акторов, КБ/с\n")
    lines.append("| Режим | " + " | ".join(KIND_NAMES[k] for k in KINDS) + " | Прочее |")
    lines.append("|---|" + "---|" * (len(KINDS) + 1))
    for m, d in runs.items():
        other = d["total_kbps"] - sum(d[f"{k}_kbps"] for k in KINDS)
        lines.append(f"| {MODE_NAMES[m]} | " + " | ".join(fmt(d[f'{k}_kbps'], 2) for k in KINDS) + f" | {fmt(other, 2)} |")

    lines.append("\n## Клиенты (среднее по клиентам)\n")
    lines.append("| Режим | Принято репликации, КБ/с | Записей/с | Видимых акторов |")
    lines.append("|---|---|---|---|")
    for m in runs:
        lines.append(f"| {MODE_NAMES[m]} | {fmt(client_avg(m, 'client_rep_kbps'))} | {fmt(client_avg(m, 'client_records_per_s'), 0)} "
                     f"| {fmt(client_avg(m, 'client_visible'))} |")

    with open(os.path.join(PROFILES, "summary.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print("\n".join(lines))

    data = []
    for m, d in runs.items():
        parts = {k: d[f"{k}_kbps"] for k in KINDS}
        parts["other"] = max(0.0, d["total_kbps"] - sum(parts.values()))
        data.append((MODE_NAMES[m], parts))
    bar_chart(data, "Исходящий трафик сервера по режимам (3 клиента)", os.path.join(GRAPHS, "traffic_by_mode.svg"))

    # Baseline на этом графике не показан: при ~955 КБ/с он сплющил бы остальные линии в ноль.
    series = []
    for m, column, label, color in (("all", "total_kbps", "Все обязательные: весь трафик", "#2f7dd1"),
                                    ("all", "rep_kbps", "Все обязательные: только акторы", "#8fb8e8"),
                                    ("everything", "total_kbps", "Все + доп.: весь трафик", "#3f8f4a"),
                                    ("everything", "rep_kbps", "Все + доп.: только акторы", "#95cf9d")):
        s = load_series(m, column)
        if s:
            series.append((label, s, color))
    line_chart(series, "Исходящий трафик сервера после оптимизаций (baseline ≈ 955 КБ/с)",
               os.path.join(GRAPHS, "traffic_timeline.svg"), "КБ/с")


if __name__ == "__main__":
    main()
