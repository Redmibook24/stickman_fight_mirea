# Практическая работа №5: оптимизация репликации (Unity)

Продолжение прототипа практики 4 (Listen Server, клиентское предсказание): на арену добавлены
**168 реплицируемых акторов**: деревья, разрушаемые ящики-укрытия, аптечки, боты и их снаряды.
Сначала снят базовый профиль трафика, затем внедрены оптимизации репликации и снят повторный профиль
по тому же сценарию.

* Unity **6000.6.0f1**, URP
* Netcode for GameObjects 2.13.3, Unity Transport 2.6, Multiplayer Tools 2.2.12, Multiplayer Play Mode 2.0.2

Отчёты:

* [docs/Replication_Baseline.md](docs/Replication_Baseline.md) — базовый профиль трафика;
* [docs/Replication_Optimization.md](docs/Replication_Optimization.md) — оптимизации, таблица «до/после», выводы;
* [docs/profiles/](docs/profiles/) — исходные данные замеров (CSV по секундам и итоговые JSON).

## Быстрый старт

1. Открыть проект в Unity 6000.6.
2. Меню **Prac5 → Собрать сцену и префабы**. Будут созданы `Assets/Prac5/Scenes/Prac5_Replication.unity`
   и префабы акторов в `Assets/Prac5/Prefabs/`.
3. Запуск двух игроков: *Window → Multiplayer → Multiplayer Play Mode*, включить *Player 2*, нажать Play;
   в одном окне **Запустить Listen Server (Host)**, в другом **Подключиться как Client**.
4. На хосте в HUD включаются и выключаются оптимизации, трафик по классам акторов виден сразу.
   **F5** — `netprofile enable / disable`: запись профиля в `%USERPROFILE%/AppData/LocalLow/DefaultCompany/prac5_replication/netprofile/`.

## Воспроизведение замеров

```powershell
# 1. Собрать headless-билд (или меню Prac5 → Собрать Windows-билд)
Unity.exe -batchmode -quit -projectPath . -executeMethod Prac5.Editor.Prac5SceneBuilder.BuildWindows
# 2. Прогнать все режимы: хост + 3 клиента, прогрев 8 с, запись 60 с на режим
powershell -ExecutionPolicy Bypass -File tools/run_profile.ps1
# 3. Собрать таблицы и графики для отчёта
python tools/make_report.py
```

## Управление

| Клавиша | Действие |
|---|---|
| WASD / стрелки | Движение (как в практике 4) |
| P | Автопилот: игрок бежит по своему круговому маршруту |
| F5 | netprofile enable / disable |
| H | Показать/скрыть HUD |

## Структура

```
Assets/Prac4/                 прототип практики 4 (игрок, предсказание, GameRules) — без изменений,
                              кроме хука автопилота в PlayerInputReader
Assets/Prac5/Scripts/Runtime/
  RepTypes.cs           классы акторов, поля, флаги оптимизаций, политики классов (NetUpdateFrequency,
                        NetCullDistance, Dormancy, NetPriority)
  ReplicatedEntity.cs   реплицируемый актор: состояние, «грязные» поля (push), клиентская интерполяция
  ArenaDirector.cs      создание 168 акторов и их серверная симуляция (боты, снаряды, ящики, аптечки)
  ReplicationServer.cs  серверный слой репликации: релевантность по сетке, dormancy, push, частоты,
                        приоритеты, сериализация
  SpatialGrid.cs        сетка Grid Spatialization
  RepWire.cs            формат записи, квантование
  ReplicationClient.cs  приём записей на клиенте
  CountingTransport.cs  UnityTransport со счётчиком исходящих байт
  TrafficProfiler.cs    netprofile: посекундный CSV и итоговый JSON
  Autopilot.cs          маршруты игроков для воспроизводимого сценария
  Prac5Bootstrap.cs     HUD и запуск эксперимента из командной строки
Assets/Prac5/Scripts/Editor/Prac5SceneBuilder.cs   сборка сцены, префабов и билда
tools/run_profile.ps1   прогон всех режимов
tools/make_report.py    таблицы и графики из docs/profiles
```
