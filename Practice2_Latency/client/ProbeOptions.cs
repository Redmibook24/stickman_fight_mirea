using System.Net;

namespace StickmanFight.Latency.Client;

/// <summary>Параметры одного прогона эксперимента.</summary>
/// <param name="ExperimentId">Имя сценария: baseline, delay_50, jitter, ...</param>
/// <param name="Server">Адрес сервера PING/PONG.</param>
/// <param name="Count">Сколько PING отправить.</param>
/// <param name="IntervalMs">Пауза между PING, мс.</param>
/// <param name="TimeoutMs">Через сколько ответ считается потерянным, мс.</param>
/// <param name="WarmupCount">
/// Сколько PING отправить «на прогрев» до начала измерений. Первый обмен в .NET
/// всегда дороже остальных из-за JIT-компиляции и ленивой инициализации сокета,
/// поэтому прогревочные пакеты в журнал не попадают.
/// </param>
public readonly record struct ProbeOptions(
    string ExperimentId,
    IPEndPoint Server,
    int Count,
    int IntervalMs,
    int TimeoutMs,
    int WarmupCount = 2)
{
    /// <summary>Проверяет значения до запуска: лучше упасть с понятным сообщением, чем собрать мусорные замеры.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExperimentId))
            throw new ArgumentException("Не задан идентификатор эксперимента (--experiment).");

        if (Count is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(Count), Count, "Число замеров должно быть в диапазоне [1; 10000].");

        if (IntervalMs is < 1 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(IntervalMs), IntervalMs, "Интервал должен быть в диапазоне [1; 60000] мс.");

        if (TimeoutMs is < 1 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(TimeoutMs), TimeoutMs, "Таймаут должен быть в диапазоне [1; 60000] мс.");

        if (WarmupCount is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(WarmupCount), WarmupCount, "Число прогревочных пакетов должно быть в диапазоне [0; 100].");
    }
}
