# Практическая работа №4: Client-Side Prediction и Server Reconciliation (Unity)

Сетевой прототип для двух и более игроков по модели «сервер — авторитет»: ходьба и **рывок с кулдауном**,
клиентское предсказание, серверная проверка, коррекция с повторным применением ввода и эмуляция задержки
и потерь.

* Unity **6000.6.0f1**, URP
* Netcode for GameObjects 2.13.3, Unity Transport 2.6, Multiplayer Tools 2.2.12 (Network Simulator),
  Multiplayer Play Mode 2.0.2

Подробности — в [docs/Prediction_Implementation.md](docs/Prediction_Implementation.md),
результаты тестов — в [docs/Prediction_Test_Matrix.md](docs/Prediction_Test_Matrix.md).

## Быстрый старт

1. Открыть проект в Unity 6000.6. Пакеты из `Packages/manifest.json` установятся автоматически.
2. Меню **Prac4 → Собрать сцену и префаб**. Будут созданы `Assets/Prac4/Scenes/Prac4_Prediction.unity`,
   префаб `Assets/Prac4/Prefabs/Player.prefab` и `NetworkManager` с `UnityTransport`, `NetworkSimulator`,
   `GameRules`, `NetLabHud`.
3. Запуск двух игроков:
   * **Multiplayer Play Mode**: *Window → Multiplayer → Multiplayer Play Mode*, включить *Player 2*, нажать Play;
   * или **сборка**: *File → Build Profiles → Windows*, включить *Development Build*, собрать и запустить
     exe рядом с редактором.
4. В одном окне нажать **Запустить Listen Server (Host)**, в другом — **Подключиться как Client**.

## Управление

| Клавиша | Действие |
|---|---|
| WASD / стрелки | Движение |
| Space / Left Shift | Рывок (кулдаун 1,5 с, только на земле) |
| F1 / F2 / F3 | Профиль сети: без эмуляции / 100 мс / 175±25 мс + 4% потерь |
| 1 | Вкл/выкл предсказание рывка |
| 2 | Вкл/выкл предсказание движения |
| C | Намеренное расхождение: клиент игнорирует кулдаун |
| X | Намеренное расхождение: клиент ускоряет себя ×1.8 |
| R | Сброс статистики |
| H | Показать/скрыть подсказку |

Синий диск под своим персонажем — последняя позиция, подтверждённая сервером.

## Структура

```
Assets/Prac4/Scripts/Runtime/
  NetTypes.cs           InputCmd, InputBatch, MoveState — что передаётся по сети
  MovementSim.cs        детерминированный шаг движения и проверка рывка (общий для клиента и сервера)
  PredictedMovement.cs  предсказание, Saved Moves, серверная проверка, коррекция, интерполяция
  PlayerInputReader.cs  сбор ввода (роль PlayerController)
  PlayerCharacter.cs    персонаж: визуал, камера, маркер сервера (роль Character)
  GameRules.cs          правила и подключение игроков (роль GameMode)
  NetLabHud.cs          HUD эксперимента, профили сети
  NetLab.cs             переключатели режимов, метрики, журнал
Assets/Prac4/Scripts/Editor/Prac4SceneBuilder.cs   автосборка сцены
docs/                   отчётные документы и медиа
```
