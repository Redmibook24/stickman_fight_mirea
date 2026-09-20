"""Построение графиков и сводной таблицы по журналу docs/latency_samples.csv.

Запуск из каталога Practice2_Latency:
    python tools/plot_latency.py

Результат: docs/graphs/rtt_timeline.png, rtt_histogram.png, rtt_boxplot.png
и сводная таблица метрик, напечатанная в консоль (её значения попадают в Latency_Report.md).
"""

import csv
import os
import statistics
import sys

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

CSV_PATH = os.path.join("docs", "latency_samples.csv")
GRAPHS_DIR = os.path.join("docs", "graphs")

# Порядок сценариев в отчёте.
SCENARIOS = ["baseline", "delay_50", "delay_100", "jitter", "loss_5", "combined"]

# Для гистограмм задание требует три сценария.
HISTOGRAM_SCENARIOS = ["baseline", "jitter", "combined"]


def read_samples(path):
    """Читает журнал, сгруппированный по сценариям, с сохранением порядка строк."""
    if not os.path.exists(path):
        sys.exit(f"Не найден журнал измерений: {path}. Сначала запустите tools/run_experiments.ps1")

    grouped = {}
    with open(path, newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle, delimiter=";"):
            grouped.setdefault(row["experiment_id"], []).append(row)
    return grouped


def smoothed_rtt(values):
    """SRTT = 0.875 * SRTT + 0.125 * RTT, первый замер берётся как есть."""
    if not values:
        return 0.0
    srtt = values[0]
    for value in values[1:]:
        srtt = 0.875 * srtt + 0.125 * value
    return srtt


def mean_jitter(values):
    """J = (1 / (n - 1)) * sum |RTT_i - RTT_{i-1}|."""
    if len(values) < 2:
        return 0.0
    return sum(abs(b - a) for a, b in zip(values, values[1:])) / (len(values) - 1)


def summarize(rows):
    rtts = [float(row["rtt_ms"]) for row in rows if row["status"] == "received"]
    timeouts = sum(1 for row in rows if row["status"] == "timeout")
    late = sum(1 for row in rows if row["status"] == "late_response")
    sent = len(rtts) + timeouts + late

    return {
        "sent": sent,
        "received": len(rtts),
        "timeouts": timeouts,
        "min": min(rtts) if rtts else 0.0,
        "max": max(rtts) if rtts else 0.0,
        "mean": statistics.fmean(rtts) if rtts else 0.0,
        "median": statistics.median(rtts) if rtts else 0.0,
        "srtt": smoothed_rtt(rtts),
        "jitter": mean_jitter(rtts),
        "loss": 100.0 * timeouts / sent if sent else 0.0,
    }


def plot_timeline(grouped):
    """RTT во времени: по одному графику на сценарий, потери отмечены крестиками."""
    figure, axes = plt.subplots(3, 2, figsize=(13, 10), sharex=False)

    for axis, scenario in zip(axes.flat, SCENARIOS):
        rows = grouped.get(scenario, [])
        received = [(float(r["sent_at_ms"]) / 1000.0, float(r["rtt_ms"]))
                    for r in rows if r["status"] == "received"]
        lost = [float(r["sent_at_ms"]) / 1000.0 for r in rows if r["status"] == "timeout"]

        if received:
            axis.plot([x for x, _ in received], [y for _, y in received],
                      marker="o", markersize=3, linewidth=1, label="RTT")
        if lost:
            axis.plot(lost, [0] * len(lost), "x", color="red", markersize=8, label="потеря")

        axis.set_title(scenario)
        axis.set_xlabel("время от начала прогона, с")
        axis.set_ylabel("RTT, мс")
        axis.grid(True, alpha=0.3)
        axis.legend(loc="upper right", fontsize=8)

    figure.suptitle("RTT во времени по сценариям (50 PING, интервал 300 мс)")
    figure.tight_layout()
    save(figure, "rtt_timeline.png")


def plot_histogram(grouped):
    figure, axes = plt.subplots(1, 3, figsize=(13, 4))

    for axis, scenario in zip(axes, HISTOGRAM_SCENARIOS):
        rtts = [float(r["rtt_ms"]) for r in grouped.get(scenario, []) if r["status"] == "received"]
        axis.hist(rtts, bins=15, edgecolor="black", alpha=0.8)
        axis.set_title(f"{scenario} (n = {len(rtts)})")
        axis.set_xlabel("RTT, мс")
        axis.set_ylabel("число замеров")
        axis.grid(True, alpha=0.3)

    figure.suptitle("Распределение RTT")
    figure.tight_layout()
    save(figure, "rtt_histogram.png")


def plot_boxplot(grouped):
    data = [[float(r["rtt_ms"]) for r in grouped.get(scenario, []) if r["status"] == "received"]
            for scenario in SCENARIOS]

    figure, axis = plt.subplots(figsize=(10, 5))
    axis.boxplot(data, tick_labels=SCENARIOS, showmeans=True)
    axis.set_ylabel("RTT, мс")
    axis.set_title("Разброс RTT по сценариям")
    axis.grid(True, axis="y", alpha=0.3)

    figure.tight_layout()
    save(figure, "rtt_boxplot.png")


def save(figure, name):
    os.makedirs(GRAPHS_DIR, exist_ok=True)
    path = os.path.join(GRAPHS_DIR, name)
    figure.savefig(path, dpi=120)
    plt.close(figure)
    print(f"сохранено: {path}")


def print_summary(grouped):
    header = ("| сценарий | отправлено | получено | таймауты | min | mean | median | max | SRTT | "
              "джиттер | потери, % |")
    print(header)
    print("|" + "---|" * 11)

    for scenario in SCENARIOS:
        rows = grouped.get(scenario, [])
        if not rows:
            continue
        s = summarize(rows)
        print(f"| {scenario} | {s['sent']} | {s['received']} | {s['timeouts']} | "
              f"{s['min']:.3f} | {s['mean']:.3f} | {s['median']:.3f} | {s['max']:.3f} | "
              f"{s['srtt']:.3f} | {s['jitter']:.3f} | {s['loss']:.2f} |")


def main():
    grouped = read_samples(CSV_PATH)

    plot_timeline(grouped)
    plot_histogram(grouped)
    plot_boxplot(grouped)

    print()
    print_summary(grouped)


if __name__ == "__main__":
    main()
