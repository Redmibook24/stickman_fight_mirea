# Практическая работа №3: надёжная доставка поверх UDP

Подтверждения `ACK`, повторная отправка по адаптивному тайм-ауту и окно дедупликации
для критичных команд игры [«Битва жердяев»](../README.md). Работа продолжает
[ПР №1](../Practice1_UDP) (команды `MOVEMENT`/`SHOOT`) и [ПР №2](../Practice2_Latency)
(телеметрия `PING`/`PONG`).

* Язык: **C# / .NET 9**, сокеты `System.Net.Sockets.UdpClient`.
* `SHOOT` отправляется надёжно, `MOVEMENT` и `PING` без подтверждения (обоснование в отчёте).
* RTO считается по Джекобсону: `RTO = SRTT + 4 * RTTVAR` в пределах 100-3000 мс,
  с правилом Карна для замеров по ACK.
* Тесты: **xUnit**, 55 тестов.

## Структура

```
Practice3_Reliability/
├── protocol/      заголовок версии 2 с requiresAck, пакеты MOVEMENT, SHOOT, ACK, проверки
├── telemetry/     из ПР №2: монотонные часы и LatencyTracker (RTT по PING/PONG)
├── transport/     из ПР №2: UDP-сокет и эмулятор сети
├── reliability/   ReliableChannel, AdaptiveTimeout, DeduplicationWindow, журнал доставки
├── server/        ACK на каждый надёжный пакет, эффект команды ровно один раз
├── client/        игровой цикл: команды, повторы по RTO, обработка ACK и недоставки
├── tests/         модульные тесты xUnit
├── tools/         run_experiments.ps1 (6 серий), plot_reliability.py (графики и таблица)
└── docs/          Protocol_Specification.md, Reliability_Protocol.md,
                   reliability_samples.csv, graphs/
```

Модули `telemetry` и `transport` перенесены из ПР №2 без изменения логики, `protocol`
расширен. Копия, а не ссылка на папку ПР №2, потому что формат заголовка поменялся:
ПР №2 должна продолжать собираться и работать со своим протоколом версии 1.

`reliability` не зависит ни от сети, ни от часов: время передаётся параметром.

## Сборка и тесты

```bash
dotnet build
```

```bash
dotnet test
```

## Запуск

Окно 1, сервер:

```bash
dotnet run --project server -- --port 9070
```

Окно 2, клиент:

```bash
dotnet run --project client -- --port 9070 --experiment demo --count 20 --csv demo.csv
```

Чтобы увидеть повторы, дубликаты и дедупликацию, добавьте потери с обеих сторон:

```bash
dotnet run --project server -- --port 9070 --emulate "loss=30"
```

```bash
dotnet run --project client -- --port 9070 --experiment demo --count 20 --emulate "loss=30" --csv demo.csv
```

### Аргументы

| Программа | Аргумент          | По умолчанию                  | Назначение                                     |
|-----------|-------------------|-------------------------------|------------------------------------------------|
| сервер    | `--port`          | `9070`                        | UDP-порт                                       |
| сервер    | `--emulate`       | нет                           | эмуляция исходящих пакетов: `delay=100,jitter=50,loss=5` |
| сервер    | `--seed`          | `20251002`                    | зерно генератора эмулятора                     |
| сервер    | `--dedup-window`  | `256`                         | размер окна дедупликации на клиента            |
| сервер    | `--quiet`         | нет                           | печатать только повторы `[DUP]` и ошибки       |
| клиент    | `--host`          | `127.0.0.1`                   | адрес сервера                                  |
| клиент    | `--port`          | `9070`                        | порт сервера                                   |
| клиент    | `--experiment`    | `baseline`                    | имя серии в журнале                            |
| клиент    | `--count`         | `50`                          | сколько команд `SHOOT` отправить               |
| клиент    | `--interval`      | `200`                         | период отправки команд, мс                     |
| клиент    | `--tick`          | `10`                          | такт игрового цикла (проверка повторов), мс    |
| клиент    | `--max-attempts`  | `5`                           | предел попыток на пакет, включая первую         |
| клиент    | `--warmup`        | `2`                           | прогревочные обмены вне журнала                |
| клиент    | `--emulate`       | нет                           | эмуляция исходящих пакетов клиента             |
| клиент    | `--seed`          | `20251003`                    | зерно генератора эмулятора клиента             |
| клиент    | `--csv`           | `docs/reliability_samples.csv`| журнал доставки                                |

## Эксперимент

```powershell
powershell -ExecutionPolicy Bypass -File tools/run_experiments.ps1
python tools/plot_reliability.py
```

Первая команда прогоняет серии `baseline`, `loss_5`, `loss_10`, `loss_20`,
`delay_100_loss_5`, `jitter_loss_10` по 50 команд и перезаписывает
`docs/reliability_samples.csv`. Вторая строит графики в `docs/graphs/` и печатает
таблицу для отчёта.

## Документация

* [docs/Protocol_Specification.md](docs/Protocol_Specification.md): формат версии 2,
  `ACK` и `requiresAck`, какие команды надёжные, порядок проверок.
* [docs/Reliability_Protocol.md](docs/Reliability_Protocol.md): алгоритм, константы RTO,
  результаты серий, графики, анализ.
* [docs/reliability_samples.csv](docs/reliability_samples.csv): журнал доставки, строка на команду.

## Пример вывода клиента при потерях 30 %

```
-> SHOOT seq=21 (requiresAck = 1), RTO 350.6 мс
   повтор SHOOT seq=21, попытка 2, RTO 350.6 мс
   повтор SHOOT seq=21, попытка 3, RTO 288.7 мс
   повтор SHOOT seq=21, попытка 4, RTO 259.6 мс
<- ACK seq=21: доставлен с попытки 4, за 917.0 мс, RTO 259.6 мс
```

и сервера:

```
[DUP] 127.0.0.1:61242: Shoot seq=21 уже обработан, эффект не применяется
```
