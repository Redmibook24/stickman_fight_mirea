# Практическая работа №7: выделенный сервер в Docker и сессии Steam

Прототип из практики 5 перенесён на выделенный сервер (Dedicated Server): отдельный Linux-процесс
без локального игрока, к которому все игроки подключаются как удалённые клиенты. Сервер собирается
отдельной серверной целью сборки, работает в Docker-контейнере и разворачивается на VM одной командой.
Жизненный цикл сессии реализован через Steam: сервер публикует себя, клиент находит сессию,
подключается, выходит и подключается снова без перезапуска игры.

* Unity **6000.6.10f1**, URP
* Netcode for GameObjects 2.13.3, Unity Transport 2.6
* Steamworks.NET **2025.165.0** (Steamworks SDK 1.65), учебный App ID 480
* Docker, базовый образ Ubuntu 22.04 (закреплён по хэшу)

Документы:

* [docs/Deployment.md](docs/Deployment.md): сборка, Docker, VM, порты и правила доступа, перезапуск;
* [docs/Steam_Integration.md](docs/Steam_Integration.md): жизненный цикл сессии, метаданные, ошибки, Steam Sockets;
* [docs/Test_Evidence.md](docs/Test_Evidence.md): результаты проверок со ссылками на логи в [docs/evidence/](docs/evidence/);
* [docs/Demo_Guide.md](docs/Demo_Guide.md): порядок показа работы на занятии;
* отчёты практики 5 (оптимизация репликации): [docs/Replication_Baseline.md](docs/Replication_Baseline.md),
  [docs/Replication_Optimization.md](docs/Replication_Optimization.md).

## Два профиля запуска

| | IP-профиль | Steam-профиль |
|---|---|---|
| Назначение | инфраструктура: сервер в Docker на VM, подключение по адресу и порту | интеграция: публикация, поиск и подключение через Steam |
| Транспорт | `UnityTransport` (UDP) | `SteamSocketsTransport` (Steam Sockets поверх UDP) |
| Как клиент находит сервер | вводит адрес вручную (диагностика) | поиск сессий Steam |
| Порты сервера | 7777/udp | 7777/udp и 27015/udp (запросы списка серверов) |
| Запуск сервера | `compose.yaml` | `compose.yaml` + `compose.steam.yaml` |

Клиент и сервер должны работать в одном профиле: транспорты разные, клиент Steam-профиля
к серверу IP-профиля не подключится, и наоборот.

## Быстрый старт

Нужны Unity 6000.6.10f1 с модулями **Linux Build Support (Mono)** и **Linux Dedicated Server Build
Support**, Docker Desktop, для Steam-профиля запущенный Steam.

```powershell
# 1. Серверный Linux-билд и Windows-клиент (из каталога Practice7_Deploy)
powershell -ExecutionPolicy Bypass -File deploy/build_server.ps1

# 2a. Сервер, IP-профиль
docker compose -f deploy/compose.yaml up -d --build

# 2b. Сервер, Steam-профиль
docker compose -f deploy/compose.yaml -f deploy/compose.steam.yaml up -d --build

# 3. Логи и остановка
docker logs -f stickman-server
docker compose -f deploy/compose.yaml down
```

Клиент: `Builds/WindowsClient/StickmanClient.exe`. Справа в меню сессий кнопки **Найти сессии**
(Steam), **В локальной сети** и отдельный раздел **Прямое подключение (диагностика)** с полем адреса.

Все проверки разом, с сохранением логов в `docs/evidence`:

```powershell
powershell -ExecutionPolicy Bypass -File deploy/test_local.ps1
```

Развёртывание на VM: [docs/Deployment.md](docs/Deployment.md#6-развёртывание-на-vm), проверка сервера
на VM: `deploy/test_vm.ps1`.

## Ключи запуска клиента (для тестов без ручных кликов)

| Ключ | Действие |
|---|---|
| `-connect 203.0.113.10:7777` | прямое подключение по адресу (IP-профиль) |
| `-autojoin` / `-autojoin-lan` | поиск сессий Steam в интернете / в локальной сети и вход в первую найденную |
| `-rejoin` | через 8 с выйти из сессии, снова найти её и подключиться (без перезапуска) |
| `-autopilot` | персонаж бегает по маршруту из практики 5 |
| `-autodash`, `-cheat-cooldown` | рывок каждые 0,6 с и клиент, игнорирующий кулдаун: проверка отказов сервера |

## Структура

```
Assets/Prac7/Scripts/Runtime/
  DedicatedServer.cs        запуск выделенного сервера (StartServer), параметры командной строки,
                            выбор профиля, штатная остановка по SIGTERM
  SteamGameServerHost.cs    регистрация сервера в Steam, публикация параметров, завершение сессии
  SteamSocketsTransport.cs  транспорт Netcode поверх Steam Sockets, фактический маршрут соединения
  SessionClient.cs          состояния клиентской сессии, прямое подключение, обработка разрывов
  SessionClient.Steam.cs    поиск сессий Steam, проверка перед входом, строка подключения
  SessionMenu.cs            меню сессий (только отображение, сетевой логики нет)
  SessionMetadata.cs        App ID, каталог игры, теги проекта и сборки
  SteamClientApi.cs         инициализация Steam API на клиенте
Assets/Prac7/Scripts/Editor/Prac7Build.cs   сборка сцены, серверного и клиентского билдов
deploy/
  Dockerfile, compose.yaml, compose.steam.yaml, entrypoint.sh   контейнер сервера
  compose.vbox.yaml         привязка к одному ядру для VM в VirtualBox поверх Hyper-V
  build_server.ps1          сборка билдов без открытия редактора
  test_local.ps1            проверки с сервером на этом ПК и сбор логов
  test_vm.ps1               проверки с сервером на VM и сбор логов
  vm_setup.sh               подготовка VM: Docker и межсетевой экран
  deploy_to_vm.sh           доставка образа на VM и запуск
Assets/Prac4, Assets/Prac5  игровой прототип практик 4-5 (персонаж, предсказание, репликация)
```

## Что сохранено из практик 4-5

* Движение и рывок по серверно-авторитетной схеме практики 4: клиент шлёт ввод, сервер проверяет
  и считает результат (`PredictedMovement.ServerApply`, `MovementSim.ValidateDash`).
* Оптимизации репликации практики 5 (сетка релевантности, dormancy, push-модель, частоты, квантование)
  работают на выделенном сервере без изменений: они не зависят ни от хоста, ни от транспорта.
  Счётчик исходящего трафика практики 5 пополняет и Steam-транспорт.

Изменения в коде практик 4-5 минимальные и помечены комментарием «ПР7»: скрытие кнопок Host/Client,
исправление двойной регистрации сетевых префабов, автоматический рывок для тестов.
