# Развёртывание выделенного сервера

## 1. Выделенный сервер и Listen Server

В практиках 4-5 сервер был **Listen Server** (Host): один из игроков одновременно играл и был
сервером (`NetworkManager.StartHost`). Здесь сервер **выделенный** (Dedicated Server): отдельный
процесс без локального игрока, все игроки подключаются к нему как удалённые клиенты
(`NetworkManager.StartServer` в [`DedicatedServer.cs`](../Assets/Prac7/Scripts/Runtime/DedicatedServer.cs)).

Сервер остаётся авторитетным: клиент присылает только ввод, сервер сам считает движение и проверяет
рывок (кулдаун, земля, повторный рывок, частота и длина вектора ввода). Код проверок не изменился
с практики 4: [`PredictedMovement.ServerApply`](../Assets/Prac4/Scripts/Runtime/PredictedMovement.cs)
и [`MovementSim.ValidateDash`](../Assets/Prac4/Scripts/Runtime/MovementSim.cs).

## 2. Серверная сборка

Сервер собирается **отдельной серверной целью сборки**: платформа `StandaloneLinux64` с подцелью
`StandaloneBuildSubtarget.Server` ([`Prac7Build.cs`](../Assets/Prac7/Scripts/Editor/Prac7Build.cs)).
Это не клиентский билд, запущенный без окна:

* при компиляции определяется символ `UNITY_SERVER`, по нему сервер стартует сам;
* из билда исключаются графика, звук, окно и IMGUI (в логе: «IMGUI module is stripped», `GfxDevice: Null`);
* билд содержит только сцену арены и нужные ей ресурсы.

Сборка состоит из компиляции кода и упаковки ресурсов сцены в каталог билда, который целиком
переносится в Docker-образ. Команда сборки сохранена в репозитории:

```powershell
powershell -ExecutionPolicy Bypass -File deploy/build_server.ps1
```

Скрипт запускает Unity 6000.6.10f1 в пакетном режиме (`-executeMethod Prac7.Editor.Prac7Build.BuildAll`)
и собирает оба билда:

| Билд | Путь | Размер |
|---|---|---|
| Linux Dedicated Server | `Builds/LinuxServer/StickmanServer.x86_64` | 96 МБ |
| Windows-клиент | `Builds/WindowsClient/StickmanClient.exe` | 104 МБ |

Рядом с каждым билдом кладётся `steam_appid.txt` с App ID 480.

Если путь к проекту длиннее 60 символов, скрипт собирает через короткую ссылку `C:\stickman_pr7`
(junction на тот же каталог): Windows не открывает файлы с путём длиннее 260 символов, а пакеты Unity
лежат глубоко в `Library\PackageCache`, и без ссылки Unity молча не читала часть файлов пакетов.

Карта сервера и GameMode: сцена `Assets/Prac5/Scenes/Prac5_Replication.unity`, правила подключения
(лимит игроков, точки появления) в `GameRules` из практики 4.

## 3. Параметры сервера

Сервер читает параметры из командной строки, контейнер передаёт их из переменных окружения
([`entrypoint.sh`](../deploy/entrypoint.sh)):

| Переменная | Ключ | По умолчанию | Назначение |
|---|---|---|---|
| `GAME_PORT` | `-port` | 7777 | UDP-порт игрового трафика |
| `SERVER_NAME` | `-serverName` | Stickman Fight PR7 | название сессии |
| `MAX_PLAYERS` | `-maxPlayers` | 4 | лимит игроков |
| `NET_PROFILE` | `-netProfile` | ip | `ip` или `steam` |

Готовность сервера видна по строке лога:

```
[Server] READY: сервер «Stickman Fight PR7» слушает 0.0.0.0:7777/udp, карта Prac5_Replication,
профиль ip, транспорт CountingTransport, мест 4, сборка 0.1.0
```

## 4. Docker

Файлы: [`deploy/Dockerfile`](../deploy/Dockerfile), [`deploy/compose.yaml`](../deploy/compose.yaml),
[`deploy/compose.steam.yaml`](../deploy/compose.steam.yaml), [`.dockerignore`](../.dockerignore).

**Образ и контейнер.** Образ это неизменяемый шаблон: ОС, библиотеки и серверный билд. Контейнер это
запущенный экземпляр образа со своим процессом, сетью и логами. Образ собирается один раз и переносится
на VM, контейнер на VM создаётся из него командой `docker compose up`.

| Требование | Как выполнено |
|---|---|
| В образ попадает только серверный пакет | копируется `Builds/LinuxServer`; `.dockerignore` закрывает всё остальное, редактора и исходников в образе нет |
| Версии зафиксированы | базовый образ `ubuntu:22.04@sha256:f64b2ffc...`, версии пакетов apt указаны явно |
| Совместимость | Ubuntu 22.04, glibc 2.35 подходит Linux-плееру Unity 6 |
| Непривилегированный пользователь | `game`, uid 10001 |
| Логи | `-logFile -` выводит лог Unity в stdout: `docker logs stickman-server`; ротация 5 файлов по 20 МБ |
| Перезапуск при аварии | `restart: unless-stopped`; PID 1 это `tini`, процесс сервера дочерний |
| Штатная остановка | `docker stop` → SIGTERM → tini → сервер: закрывает соединения, снимает сессию Steam, код выхода 0 |
| Ограничения ресурсов | 1 ГБ памяти, 1,5 CPU |

Steam-профилю нужна библиотека `steamclient.so` из пакета Steamworks SDK Redist (app 1007). Её раздаёт
Valve через официальный steamcmd: она скачивается в отдельном этапе сборки образа, в итоговый образ
копируется только сам файл. Версию app 1007 Valve закрепить не позволяет.

Размер итогового образа около 265 МБ, сервер в работе занимает около 130 МБ памяти и 3% CPU.

**Порт процесса, публикация порта и правила облака** это три разных уровня:

1. *порт процесса*: сервер внутри контейнера слушает 7777/udp;
2. *публикация порта контейнера*: `ports: "7777:7777/udp"` в compose открывает этот порт на хосте.
   `EXPOSE` в Dockerfile только документирует порт и сам ничего не публикует;
3. *правила облачной сети* (группа безопасности VM) решают, дойдёт ли пакет из интернета до VM.

## 5. Порты и правила доступа

| Порт | Протокол | Профиль | Кто должен иметь доступ |
|---|---|---|---|
| 7777 | UDP | оба | все игроки |
| 27015 | UDP | Steam | все: на него приходят запросы списка серверов Steam |
| 22 | TCP | VM | только административный адрес |

Больше на VM ничего не открывается.

**Docker и межсетевой экран.** Опубликованный порт контейнера Docker открывает собственными правилами
iptables в обход ufw. Поэтому ufw на VM защищает SSH и службы самой VM, а настоящая граница доступа к
игровым портам это группа безопасности облака. Настройка межсетевого экрана сама по себе не доказывает
защищённость: доступность проверяется игровым подключением снаружи, а не состоянием `running`, ping
или проверкой TCP-порта. Отсутствие ответа на произвольную UDP-датаграмму тоже ничего не доказывает:
сервер отвечает только на пакеты своего протокола.

## 6. Развёртывание на VM

### 6.1. Общий порядок (любая VM)

1. Создать VM с Ubuntu 22.04/24.04. Для облачной VM в группе безопасности открыть TCP 22 только
   с адреса администратора, UDP 7777 и UDP 27015 для всех.
2. Подготовить VM (Docker и ufw):

   ```bash
   scp deploy/vm_setup.sh user@<IP_VM>:~
   ssh user@<IP_VM> "ADMIN_IP=<ваш_внешний_IP> bash vm_setup.sh"
   ```

3. Собрать билды (`deploy/build_server.ps1`), доставить образ и запустить сервер
   (Git Bash, каталог `Practice7_Deploy`):

   ```bash
   VM=user@<IP_VM> PROFILE=steam bash deploy/deploy_to_vm.sh
   ```

   Скрипт собирает образ локально, передаёт его на VM через SSH (`docker save | docker load`),
   копирует compose-файлы выбранного профиля (`ip` или `steam`) и запускает
   `docker compose up -d --no-build`. На VM не нужны ни Unity, ни исходники.
4. Проверка на VM: `powershell -ExecutionPolicy Bypass -File deploy/test_vm.ps1 -Vm user@<IP_VM> -SshPort 22 -SshKey <ключ> -ExtraCompose ""`.

### 6.2. Учебный стенд: VM в VirtualBox и точка доступа ноутбука

Проверка проводится в аудитории, где у стенда нет публичного IP и нет доступа к роутеру. Поэтому
VM работает в VirtualBox на ноутбуке, а внешняя сеть для клиентов это точка доступа Windows
(«Мобильный хот-спот»), к которой подключается второй ПК:

```
телефон (мобильный интернет) ──Wi-Fi──► ноутбук ──точка доступа──► ПК второго игрока
                                          └─ VirtualBox (NAT) → VM Ubuntu 22.04 → Docker → сервер
```

| Параметр | Значение |
|---|---|
| VM | Ubuntu 22.04.5, 2 vCPU, 4 ГБ, Docker 29.8 |
| Сеть VM | NAT VirtualBox с пробросом портов: UDP 7777 и UDP 27015 на все адреса ноутбука, TCP 2222 → 22 только на `127.0.0.1` |
| Вход на VM | только по SSH-ключу и только с самого ноутбука |
| Адрес сервера для игроков | адрес ноутбука в сети точки доступа; игроки его не вводят, а находят сессию поиском Steam «В локальной сети» |

NAT выбран вместо сетевого моста: адрес сервера для игроков всегда совпадает с адресом ноутбука,
в какой бы сети тот ни был, и при смене сети VM не нужно перенастраивать.

Проброс портов добавляется один раз при выключенной VM:

```bash
VBoxManage modifyvm <имя_VM> --natpf1 "stickman-game,udp,,7777,,7777"
VBoxManage modifyvm <имя_VM> --natpf1 "stickman-steam-query,udp,,27015,,27015"
```

Входящие UDP 7777 и 27015 на ноутбуке разрешаются в брандмауэре Windows (PowerShell от администратора):

```powershell
New-NetFirewallRule -DisplayName "Stickman PR7 UDP" -Direction Inbound -Protocol UDP -LocalPort 7777,27015 -Action Allow
```

Развёртывание и проверка на стенде:

```bash
VM=user@127.0.0.1 SSH_PORT=2222 SSH_KEY=~/.ssh/<ключ_VM> PROFILE=steam VBOX=1 bash deploy/deploy_to_vm.sh
```

```powershell
powershell -ExecutionPolicy Bypass -File deploy/test_vm.ps1
```

**VirtualBox поверх Hyper-V.** Если в Windows включён Hyper-V (его включают WSL2 и Docker Desktop),
VirtualBox не получает аппаратную виртуализацию напрямую и работает через Hyper-V
(в журнале VM: `HM: Attempting fall back to NEM: VT-x is not available`). В этом режиме счётчик тактов
на разных виртуальных ядрах расходится, и Steam Sockets видит «время назад»: в логе сервера появляется
`steamnetworkingsockets_lowlevel_misc.cpp (523) : usecElapsed >= 0`, подключения изредка обрываются
по тайм-ауту, а процесс сервера может зависнуть при выходе. Поэтому на стенде добавляется
[`compose.vbox.yaml`](../deploy/compose.vbox.yaml): сервер привязан к одному ядру VM (`cpuset: "0"`).
Результат измерен: 74 таких сообщения за прогон без привязки и 0 с ней
([06b](evidence/06b_vm_virtualbox_without_cpuset/server.log) и [06](evidence/06_vm_virtualbox/server.log)).
Перед запуском VM Docker Desktop нужно закрыть: две виртуальные машины поверх Hyper-V работают заметно
медленнее, VM загружалась до 5 минут.

**Отступление от методички.** Методичка предполагает VM с публичным IP и подключение клиентов через
интернет. У учебного стенда публичного IP нет, поэтому:

* клиенты подключаются с других компьютеров через сеть точки доступа ноутбука, то есть из сети,
  внешней по отношению к VM и к контейнеру, но не из интернета;
* поиск сессий Steam в интернете проверен до получения списка от мастер-сервера: мастер-сервер
  возвращает нашу сессию, а прямой опрос сервера по публичному IP из той же сети не проходит
  (NAT hairpin, см. [Test_Evidence.md](Test_Evidence.md)). Полный цикл показывается поиском в локальной
  сети, это тот же интерфейс `ISteamMatchmakingServers` и та же строка подключения от Steam;
* роль группы безопасности облака на стенде играют брандмауэр Windows и правила проброса VirtualBox:
  наружу открыты только UDP 7777 и 27015, SSH доступен только с самого ноутбука.

На облачной VM с публичным IP тот же образ и те же скрипты работают без изменений (раздел 6.1),
а клиенты находят сессию поиском через интернет.

## 7. Перезапуск и остановка

```bash
docker logs --tail 100 -f stickman-server                          # логи
docker inspect --format '{{.RestartCount}}' stickman-server        # число перезапусков
docker exec stickman-server sh -c 'kill -9 $(pgrep -f StickmanServer.x86_64)'   # имитация аварии
docker compose -f deploy/compose.yaml stop                         # штатная остановка
```

Проверено и на ПК, и на VM: после `kill -9` контейнер поднимается сам за несколько секунд
(`RestartCount = 1`), `docker compose stop` завершает сервер за 1-2 с с кодом 0. Логи проверок: [Test_Evidence.md](Test_Evidence.md).

## 8. Служебные сообщения в логе запуска

| Сообщение | Почему это не ошибка |
|---|---|
| `steamclient.so: cannot open shared object file` и затем `Loaded '/srv/game/.steam/sdk64/steamclient.so' OK` | Steam сначала ищет библиотеку рядом с программой, затем в `~/.steam/sdk64`, где она и лежит |
| `[S_API FAIL] Tried to access Steam interface SteamNetworkingUtils004 before SteamAPI_Init` | Steamworks.NET при запуске игрового сервера сначала пробует клиентский интерфейс, затем берёт серверный (`Steam.cs` пакета, строки 603-609); на работу не влияет |
| `OnGUI function detected ... IMGUI module is stripped` | в серверной сборке нет интерфейса, меню клиента на сервере не нужно |
| `flock /sys/devices/virtual/dmi/id/... LOCK_SH failed. errno = 13` | Steam читает сведения об оборудовании, а сервер работает от непривилегированного пользователя без доступа к ним; на работу не влияет |
| `Unable to load player prefs` | первый запуск: файла настроек Unity ещё нет, сервер ими не пользуется |
