"""Графики и сводная таблица по журналу docs/reliability_samples.csv.

Запуск из каталога Practice3_Reliability:
    python tools/plot_reliability.py

Результат: docs/graphs/attempts_vs_loss.png, docs/graphs/rto_jitter_loss_10.png
и таблица для docs/Reliability_Protocol.md, напечатанная в консоль.
"""

import csv
import os
import statistics
import sys

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

CSV_PATH = os.path.join("docs", "reliability_samples.csv")
GRAPHS_DIR = os.path.join("docs", "graphs")

SERIES = ["baseline", "loss_5", "loss_10", "loss_20", "delay_100_loss_5", "jitter_loss_10"]

# Потери в каждую сторону, %, как они заданы в tools/run_experiments.ps1.
LOSS_PERCENT = {
    "baseline": 0, "loss_5": 5, "loss_10": 10, "loss_20": 20,
    "delay_100_loss_5": 5, "jitter_loss_10": 10,
}

MAX_ATTEMPTS = 5
FIXED_TIMEOUT_MS = 1000   # тайм-аут ожидания ответа из ПР №2


def read_records(path):
    if not os.path.exists(path):
        sys.exit(f"Не найден журнал: {path}. Сначала запустите tools/run_experiments.ps1")

    grouped = {}
    with open(path, newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle, delimiter=";"):
            grouped.setdefault(row["experiment_id"], []).append(row)
    return grouped


def summarize(rows):
    attempts = [int(r["attempts"]) for r in rows]
    delivered = [r for r in rows if r["status"] == "delivered"]
    by_time = sorted(rows, key=lambda r: float(r["resolved_at_ms"]))

    return {
        "sent": len(rows),
        "first_try": sum(1 for r in delivered if int(r["attempts"]) == 1),
        "retransmits": sum(a - 1 for a in attempts),
        "failed": sum(1 for r in rows if r["status"] == "failed"),
        "avg_attempts": statistics.fmean(attempts),
        "avg_rto": statistics.fmean(float(r["rto_ms"]) for r in rows),
        "avg_tta": statistics.fmean(float(r["time_to_ack_ms"]) for r in delivered) if delivered else 0.0,
        "final_rto": float(by_time[-1]["rto_ms"]),
    }


def expected_attempts(loss_percent, max_attempts=MAX_ATTEMPTS):
    """Ожидаемое число отправок одного пакета при независимых потерях p в каждую сторону.

    Обмен удачен, если дошла и команда, и ACK: q = (1 - p)^2.
    Попытка k нужна, если все k - 1 предыдущих обменов неудачны, поэтому
    E[attempts] = сумма (1 - q)^k по k от 0 до max_attempts - 1.
    """
    p = loss_percent / 100.0
    q = (1 - p) ** 2
    return sum((1 - q) ** k for k in range(max_attempts))


def plot_attempts_vs_loss(grouped):
    figure, axis = plt.subplots(figsize=(8, 5))

    theory_x = [x / 2 for x in range(0, 51)]
    axis.plot(theory_x, [expected_attempts(x) for x in theory_x], "--", color="gray",
              label="теория: независимые потери в обе стороны")

    main = [s for s in ["baseline", "loss_5", "loss_10", "loss_20"] if s in grouped]
    axis.plot([LOSS_PERCENT[s] for s in main],
              [summarize(grouped[s])["avg_attempts"] for s in main],
              "o-", markersize=7, label="измерено: серии только с потерями")

    for series, marker in (("delay_100_loss_5", "s"), ("jitter_loss_10", "^")):
        if series in grouped:
            axis.plot(LOSS_PERCENT[series], summarize(grouped[series])["avg_attempts"],
                      marker, markersize=9, linestyle="none", label=f"измерено: {series}")

    axis.set_xlabel("потери в каждую сторону, %")
    axis.set_ylabel("среднее число попыток на пакет")
    axis.set_title("Среднее число попыток в зависимости от потерь")
    axis.set_ylim(bottom=0.9)
    axis.grid(True, alpha=0.3)
    axis.legend(fontsize=8)

    figure.tight_layout()
    save(figure, "attempts_vs_loss.png")


def plot_rto_jitter(grouped):
    rows = sorted(grouped.get("jitter_loss_10", []), key=lambda r: float(r["resolved_at_ms"]))
    if not rows:
        return

    t = [float(r["resolved_at_ms"]) / 1000.0 for r in rows]
    figure, axis = plt.subplots(figsize=(10, 5))

    axis.plot(t, [float(r["rto_ms"]) for r in rows], "-", linewidth=2, label="RTO")
    axis.plot(t, [float(r["srtt_ms"]) for r in rows], "-", linewidth=1, label="SRTT")

    first = [r for r in rows if r["status"] == "delivered" and int(r["attempts"]) == 1]
    retried = [r for r in rows if r["status"] == "delivered" and int(r["attempts"]) > 1]
    axis.plot([float(r["resolved_at_ms"]) / 1000.0 for r in first],
              [float(r["time_to_ack_ms"]) for r in first],
              "o", markersize=4, alpha=0.7, label="время до ACK, с первой попытки")
    axis.plot([float(r["resolved_at_ms"]) / 1000.0 for r in retried],
              [float(r["time_to_ack_ms"]) for r in retried],
              "x", markersize=8, color="red", label="время до ACK, после повтора")

    axis.axhline(FIXED_TIMEOUT_MS, linestyle="--", color="gray", label="фиксированный тайм-аут ПР №2")

    axis.set_xlabel("время от начала серии, с")
    axis.set_ylabel("мс")
    axis.set_title("RTO во времени, серия jitter_loss_10 (задержка 50-150 мс, потери 10 %)")
    axis.set_ylim(0, FIXED_TIMEOUT_MS * 1.1)
    axis.grid(True, alpha=0.3)
    axis.legend(fontsize=8, loc="center right")

    figure.tight_layout()
    save(figure, "rto_jitter_loss_10.png")


def save(figure, name):
    os.makedirs(GRAPHS_DIR, exist_ok=True)
    path = os.path.join(GRAPHS_DIR, name)
    figure.savefig(path, dpi=120)
    plt.close(figure)
    print(f"сохранено: {path}")


def print_summary(grouped):
    print("| Серия | Отправлено | Доставлено с 1-й попытки | Retransmit total | Failed | "
          "Avg attempts | Avg RTO, мс | Time-to-ACK, мс | RTO в конце, мс | Теория attempts |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")

    for series in SERIES:
        if series not in grouped:
            continue
        s = summarize(grouped[series])
        share = 100.0 * s["first_try"] / s["sent"]
        print(f"| {series} | {s['sent']} | {s['first_try']} ({share:.0f} %) | {s['retransmits']} | "
              f"{s['failed']} | {s['avg_attempts']:.2f} | {s['avg_rto']:.1f} | {s['avg_tta']:.1f} | "
              f"{s['final_rto']:.1f} | {expected_attempts(LOSS_PERCENT[series]):.2f} |")

    print()
    print("Оценка: время до ACK, если бы повтор ждал фиксированные 1000 мс, как тайм-аут ПР №2")
    for series in SERIES:
        rows = [r for r in grouped.get(series, []) if r["status"] == "delivered"]
        if not rows:
            continue
        # Последняя отправка дошла за то же время, что и с адаптивным RTO; меняется только
        # ожидание перед каждым повтором: 1000 мс вместо фактического RTO.
        measured = statistics.fmean(float(r["time_to_ack_ms"]) for r in rows)
        extra = statistics.fmean((int(r["attempts"]) - 1) * (FIXED_TIMEOUT_MS - float(r["rto_ms"])) for r in rows)
        print(f"  {series}: измерено {measured:.1f} мс, с фиксированным тайм-аутом около {measured + extra:.1f} мс")


def main():
    grouped = read_records(CSV_PATH)
    plot_attempts_vs_loss(grouped)
    plot_rto_jitter(grouped)
    print()
    print_summary(grouped)


if __name__ == "__main__":
    main()
