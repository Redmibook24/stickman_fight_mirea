using System.Net;

namespace StickmanFight.Reliable.Client;

/// <summary>Параметры серии.</summary>
/// <param name="ExperimentId">Имя серии в журнале: baseline, loss_5, ...</param>
/// <param name="Server">Адрес сервера.</param>
/// <param name="Count">Сколько надёжных команд SHOOT отправить.</param>
/// <param name="IntervalMs">Период отправки команд, мс.</param>
/// <param name="TickMs">Шаг игрового цикла, мс: с этой частотой проверяется, кому пора на повтор.</param>
/// <param name="MaxAttempts">Предел попыток на один пакет, включая первую отправку.</param>
/// <param name="WarmupCount">
/// Сколько пар PING и подтверждаемого MOVEMENT отправить до начала серии. Первый обмен
/// в .NET дороже остальных из-за JIT-компиляции (без прогрева первый ACK шёл 240 мс
/// вместо 1 мс) и испортил бы начальную оценку RTO, поэтому прогрев в журнал не попадает.
/// </param>
public readonly record struct ClientOptions(
    string ExperimentId,
    IPEndPoint Server,
    int Count,
    int IntervalMs,
    int TickMs,
    int MaxAttempts,
    int WarmupCount = 2)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExperimentId))
            throw new ArgumentException("Не задано имя серии (--experiment).");

        if (Count is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(Count), Count, "Число команд должно быть в диапазоне [1; 10000].");

        if (IntervalMs is < 10 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(IntervalMs), IntervalMs, "Интервал должен быть в диапазоне [10; 60000] мс.");

        if (TickMs < 1 || TickMs > IntervalMs)
            throw new ArgumentOutOfRangeException(nameof(TickMs), TickMs, "Шаг цикла должен быть от 1 мс и не больше интервала команд.");

        if (MaxAttempts is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "Число попыток должно быть в диапазоне [1; 50].");

        if (WarmupCount is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(WarmupCount), WarmupCount, "Число прогревочных обменов должно быть в диапазоне [0; 100].");
    }
}
