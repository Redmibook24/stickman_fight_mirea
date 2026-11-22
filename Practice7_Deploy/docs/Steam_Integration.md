# Интеграция со Steam: жизненный цикл сессии

Steam подключён через Steamworks.NET 2025.165.0 (Steamworks SDK 1.65) с учебным App ID **480**.
Сервер работает как Steam GameServer (`SteamGameServer`), клиент использует пользовательский
Steam API (`SteamAPI`, нужен запущенный Steam). Обе стороны прочитаны из одного файла настроек
[`SessionMetadata.cs`](../Assets/Prac7/Scripts/Runtime/SessionMetadata.cs).

## 1. Соответствие этапов жизненного цикла

| Этап | Где реализован | Вызовы Steam / Netcode |
|---|---|---|
| Создание сессии выделенным сервером | `SteamGameServerHost.Begin` | `GameServer.InitEx(..., eServerModeAuthentication, ...)`, `LogOnAnonymous` |
| Публикация параметров | `SteamGameServerHost.Begin` | `SetServerName`, `SetMapName`, `SetMaxPlayerCount`, `SetModDir`, `SetGameTags`, `SetKeyValue`, `SetAdvertiseServerActive(true)` |
| Поиск с фильтрами проекта и сборки | `SessionClient.SearchSessions` | `RequestInternetServerList` с фильтрами `gamedir` и `gametagsand`; `RequestLANServerList` для локальной сети |
| Список сессий | `SessionMenu` | название, карта, режим, сборка, игроки/мест, пинг |
| Присоединение | `SessionClient.JoinSession` | `PingServer`: повторная проверка, что сессия жива и не заполнена |
| Получение адреса подключения | `OnPingResponded` | `servernetadr_t.GetConnectionAddressString()` из ответа Steam о сервере |
| Переход клиента на сервер | `SessionClient.BeginJoin` | `NetworkManager.StartClient` через `SteamSocketsTransport`; карту клиент получает от сервера (синхронизация сцен Netcode) |
| Выход | `SessionClient.Leave` | `NetworkManager.Shutdown`, очистка адреса, ошибки и состояния |
| Повторный поиск и вход без перезапуска | `SessionClient` | после `Leave` состояние `Idle`, все запросы Steam освобождены (`ReleaseRequest`) |
| Учёт игроков | `SteamGameServerHost` | `CreateUnauthenticatedUserConnection`, `BUpdateUserData`, `SendUserDisconnect_DEPRECATED`: счётчик игроков в списке Steam |
| Уничтожение сессии при остановке | `SteamGameServerHost.End` | `SetAdvertiseServerActive(false)`, `LogOff`, `GameServer.Shutdown` |

Адрес подключения клиент не вводит и не собирает сам: его возвращает Steam в описании найденного
сервера, и именно этот адрес выводится в лог как «строка подключения от Steam».

## 2. Публикуемые параметры

| Параметр | Значение | Как выставлен |
|---|---|---|
| Название | `SERVER_NAME`, по умолчанию «Stickman Fight PR7» | `SetServerName` |
| Карта | `Prac5_Replication` | `SetMapName` (имя активной сцены) |
| Режим игры | `Deathmatch` | тег `mode:` и ключ `GAME_MODE` |
| Вместимость | `MAX_PLAYERS`, по умолчанию 4 | `SetMaxPlayerCount` |
| Каталог игры | `stickman_pr7` | `SetModDir`, фильтр `gamedir` |
| Тег проекта | `MultiplayerCourse_Stickman` | тег `proj:` и ключ `PROJECT_TAG` |
| Идентификатор сборки | `Application.version` (0.1.0) | тег `build:` и ключ `BUILD_ID` |

Строка тегов: `proj:MultiplayerCourse_Stickman,build:0.1.0,mode:Deathmatch`. Клиент ищет только
сессии с совпадающими проектом и сборкой: под App ID 480 одновременно работают тысячи чужих серверов,
и без фильтров список был бы заполнен ими. Сборка в фильтре не даёт подключиться к серверу
несовместимой версии. Мастер-сервер Steam применяет фильтры сам, а клиент дополнительно перепроверяет
каталог, проект и сборку каждого ответа: в LAN-поиске фильтров нет вовсе.

## 3. Состояния клиента

```
Idle → Searching → ResultsReady → Joining → InGame
          ↓                          ↓         ↓
        Error                      Error     Error (обрыв) / Idle (выход)
```

| Состояние | Что показывает меню | Что можно сделать |
|---|---|---|
| `Idle` | кнопки «Найти сессии», «В локальной сети» и прямое подключение | искать, подключаться |
| `Searching` | «поиск... найдено: N» | ничего: повторное нажатие игнорируется (`IsBusy`) |
| `ResultsReady` | список сессий или «Сессий не найдено» | войти, искать снова |
| `Joining` | «Подключение к адрес:порт» | ничего, ждать результата или тайм-аута |
| `InGame` | сервер, транспорт и маршрут соединения | «Выйти из сессии» |
| `Error` | причина ошибки | «Назад к поиску» |

Меню ([`SessionMenu.cs`](../Assets/Prac7/Scripts/Runtime/SessionMenu.cs)) только вызывает методы
`SessionClient` и отображает его состояние, сетевой логики в нём нет.

## 4. Обработка ошибок

| Ситуация | Что видит игрок | Как обработано |
|---|---|---|
| Steam не запущен | «Steam недоступен: ...» | `SteamAPI.InitEx` вернул ошибку |
| Поиск не начался | «Steam не начал поиск сессий» | недействительный дескриптор запроса |
| Сессий нет | пустой список, «сессий не найдено» | `ResultsReady` с нулём результатов |
| Поиск завис | «поиск не завершился за 15 с» | тайм-аут, `CancelQuery` и `ReleaseRequest` |
| Поздний ответ старого поиска | ничего | колбэк сверяет дескриптор запроса и состояние |
| Сервер в списке, но не отвечает | строка в логе «сервер #N не ответил» | `OnServerFailedToRespond` |
| Сессия исчезла после поиска | «сессия ... исчезла: сервер не отвечает» | `PingServer` перед входом |
| Сессия заполнена | «сессия ... заполнена: 4/4»; в списке кнопка «Заполнена» | проверка ответа `PingServer`; сервер вдобавок отказывает сам (`GameRules`) |
| Сервер не ответил на подключение | «сервер ... не ответил за 10 с» | тайм-аут интерфейса отменяет попытку через `Shutdown` |
| Сервер отказал | «сервер отклонил подключение: ...» | текст причины от сервера |
| Сервер штатно остановлен | «сервер ... завершил работу» | Steam Sockets: `ClosedByPeer` |
| Сервер упал или пропала сеть | «сервер ... перестал отвечать (обрыв связи или падение сервера)» | Steam Sockets: `ProblemDetectedLocally`; UTP: тайм-аут |
| Регистрация сервера в Steam не удалась | лог сервера: «ОШИБКА регистрации в Steam» | колбэк `SteamServerConnectFailure_t`, Steam повторяет попытку сам |

После любой ошибки игрок возвращается к поиску кнопкой «Назад к поиску», перезапуск игры не нужен.

## 5. Транспорт Steam Sockets

Игровой трафик Steam-профиля идёт через **Steam Networking Sockets**: класс транспорта
[`SteamSocketsTransport`](../Assets/Prac7/Scripts/Runtime/SteamSocketsTransport.cs) реализует
`NetworkTransport` Netcode for GameObjects поверх `SteamGameServerNetworkingSockets` на сервере
и `SteamNetworkingSockets` на клиенте.

| Вопрос | Ответ |
|---|---|
| Класс транспорта | `Prac7.SteamSocketsTransport` (сервер пишет его имя в строке READY) |
| Тип адреса | IP-адрес и UDP-порт (`SteamNetworkingIPAddr`): сервер слушает `CreateListenSocketIP` на 7777, клиент подключается `ConnectByIPAddress` по строке от Steam |
| Каналы | ненадёжные сообщения Netcode идут как `UnreliableNoNagle`, остальные как `ReliableNoNagle` |
| Проверка маршрута | после установки соединения транспорт читает `GetConnectionInfo` и `GetDetailedConnectionStatus` и пишет в лог удалённый адрес и признак relay; меню показывает маршрут в состоянии `InGame` |
| Трафик практики 5 | транспорт пополняет счётчики `CountingTransport` (байты и вызовы отправки) |

Подтверждённый результат: **Steam Sockets работает, соединение прямое** (в локальной сети:
`адрес 192.168.56.1:7777, relay не используется`).
**Relay экспериментально не подтверждено**: соединение по IP-адресу (`ConnectByIPAddress`) идёт
напрямую по UDP и через ретрансляторы Steam Datagram Relay не проходит. Relay доступен только для
подключения по SteamID (`ConnectP2P`) и выделенных серверов в сети Valve, учебный App ID 480 этого
не даёт. Поэтому серверу на VM нужен открытый UDP-порт, и ретранслятор этого не заменяет.

Чтобы клиент и сервер не оказались на разных транспортах, транспорт выбирается профилем запуска:
сервер ставит его в `DedicatedServer` по `-netProfile`, клиент в `SessionClient`: найденная через
Steam сессия всегда открывается через `SteamSocketsTransport`, прямое подключение по адресу через
`UnityTransport`.

## 6. Ограничения учебного стенда

* **App ID 480** общий для всех разработчиков Steamworks. Сервер регистрируется с продуктом
  `SteamworksExample` и отдельным каталогом игры, чужие серверы отсекаются фильтрами из раздела 2.
* **Поиск через интернет из той же сети, что и сервер.** Мастер-сервер Steam возвращает нашу сессию,
  но затем клиент опрашивает сервер по публичному IP, а домашний роутер не пропускает пакет из
  локальной сети на свой же внешний адрес (NAT hairpin). Поэтому на одном ПК сессия видна в логе
  как «не ответил на запрос», а полный цикл проверяется LAN-поиском. Для сервера на VM с публичным
  IP этого ограничения нет.
* Клиенту нужен запущенный Steam с вошедшим пользователем: Steam API клиента работает через него.
  Серверу Steam не нужен, он входит анонимно (`LogOnAnonymous`).

## 7. Где сервер проверяет действия игроков

Steam отвечает только за поиск сессии и доставку пакетов, правила игры проверяет сервер, как и
в практике 4: [`PredictedMovement.ServerApply`](../Assets/Prac4/Scripts/Runtime/PredictedMovement.cs)
(движение и обрезка вектора ввода) и [`MovementSim.ValidateDash`](../Assets/Prac4/Scripts/Runtime/MovementSim.cs)
(кулдаун, земля, повторный рывок). Проверка на развёрнутом сервере: сценарий 02 в
[Test_Evidence.md](Test_Evidence.md).
