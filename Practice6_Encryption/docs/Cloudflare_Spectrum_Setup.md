# Cloudflare Spectrum: защита игрового UDP-порта (ПР№6, этапы 3–4)

## 0. Важное ограничение: тариф

Spectrum доступен только на платных тарифах, а **произвольный UDP-порт** — только на **Enterprise**.
Pro и Business поддерживают лишь отдельные протоколы (по документации — «selected protocols only»),
UDP среди них нет; на бесплатном тарифе Spectrum нет вовсе. Simple Proxy Protocol, через который origin узнаёт реальный IP клиента, тоже требует Enterprise.
Источники: [Spectrum — Get started](https://developers.cloudflare.com/spectrum/get-started/),
[Enable Proxy Protocol](https://developers.cloudflare.com/spectrum/how-to/enable-proxy-protocol/).

Поэтому:

* шаги 1–6 ниже — точная инструкция для аккаунта с Enterprise и Spectrum (учебный аккаунт кафедры
  или trial от Cloudflare). Места для скриншотов панели отмечены **[скриншот]**;
* вся логика, которая зависит от нас, а не от тарифа, реализована и проверена **локальной эмуляцией
  Spectrum** (раздел 5). Это разбор Simple Proxy Protocol на origin, ответы с тем же заголовком,
  отказ от пакетов в обход прокси и подключение по доменному имени.

## 1. Схема «клиент → Cloudflare → origin»

```
 Игрок (любой IP)                 Cloudflare (anycast)                           Origin-VM (cloud.ru)
 udp_client game.example.com:9000 ─UDP 9000─►  Spectrum app "game"  ─UDP 9000 + SPP─►  udp_server --spp
                                   DDoS-фильтрация L3/L4             с адресов из        файрвол: UDP 9000 только
                                   (Network-layer DDoS ruleset)      cloudflare.com/ips   от Cloudflare, SSH — от админа
 ◄────────────────────────────────  снимает SPP  ◄──── ответ + тот же SPP ─────────────
```

* Клиент знает только `game.example.com`: DNS отдаёт anycast-адрес Cloudflare, реальный IP VM не публикуется.
* Cloudflare фильтрует объёмные атаки на своей сети и пересылает на origin только «чистый» трафик.
* Origin принимает игровой порт только с диапазонов Cloudflare, поэтому прямую атаку по IP VM
  отбрасывает файрвол.

**Открытые порты VM:**

| Порт | Протокол | Откуда разрешён | Назначение |
|---|---|---|---|
| 9000 | UDP | только диапазоны Cloudflare ([ips-v4](https://www.cloudflare.com/ips-v4), [ips-v6](https://www.cloudflare.com/ips-v6)) | игровой сервер `udp_server --spp` |
| 22 | TCP | только IP администратора | SSH |
| остальные | — | закрыты (`default deny incoming`) | — |

Правила задаются скриптом [`tools/origin_firewall.sh`](../tools/origin_firewall.sh) (ufw).
В облаке cloud.ru те же правила стоит продублировать в группе безопасности VM, чтобы трафик
отсекался ещё до виртуальной машины.

## 2. Пошаговая настройка

1. **Origin.** Поднять VM (Ubuntu 24.04), собрать сервер:
   ```bash
   sudo apt install -y g++ cmake libssl-dev
   cmake -S . -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build -j
   ./build/udp_server --spp --sign-key server_ed25519.pem   # печатает pin для клиентов
   ```
   Включить файрвол: `sudo ADMIN_IP=<ваш IP> bash tools/origin_firewall.sh`.
2. **Домен.** Добавить домен в Cloudflare (*Add a site*) и сменить NS-серверы у регистратора на выданные
   Cloudflare. Дождаться статуса *Active*. **[скриншот: обзор зоны со статусом Active]**
3. **Spectrum-приложение.** *Spectrum → Create an application*:
   * Application type: **UDP**;
   * Hostname: `game.example.com`;
   * Edge port: **9000**;
   * Origin: **IP VM**, порт **9000**;
   * Proxy Protocol: **Simple Proxy Protocol** (иначе сервер видит только IP Cloudflare);
   * IP Access Rules — по желанию (блокировка стран и ASN).

   **[скриншот: настройки приложения]**
4. **DNS.** Spectrum сам создаёт запись `game.example.com`, проксированную через Spectrum (оранжевое облако,
   тип Spectrum). Проверить в *DNS → Records*, что A-записи с реальным IP VM **нет** (ни у этого имени,
   ни у других: `www`, `mail` и т. п.). **[скриншот: DNS-записи]**
5. **Клиент.** Указать адрес сервера `game.example.com:9000` вместо `127.0.0.1`. Клиент разрешает имя
   через `getaddrinfo`, отдельной настройки не нужно:
   ```bash
   ./build/udp_client game.example.com:9000 --pin <pin из лога сервера>
   ```
6. **Проверка.** В логе сервера должен быть формат `[IP клиента:порт via IP Cloudflare:порт]`: первый
   адрес — реальный адрес игрока из SPP, второй — адрес узла Cloudflare. Прямое подключение к IP VM
   должно не проходить вовсе: пакеты отбрасывает файрвол. **[скриншот: лог сервера и Analytics приложения]**

## 3. Правила фильтрации: Network-layer DDoS Attack Protection

*Security → DDoS → Network-layer DDoS Attack Protection* — управляемый набор правил Cloudflare
(Managed Ruleset) для L3/L4-атак. Он включён всегда; переопределить можно чувствительность и действие
для всего набора, для отдельных тегов или правил.

| Параметр | Выбор | Почему |
|---|---|---|
| Чувствительность набора | **High** (по умолчанию) | игровой трафик очень регулярен: маленькие пакеты (17–66 байт) 20–60 раз в секунду от каждого игрока, без больших всплесков. Атаки (UDP flood, amplification с портов 53/123/1900/11211, пакеты не нашего размера) хорошо отличимы от нормы, поэтому высокая чувствительность почти не даёт ложных срабатываний |
| Действие | **Block** (по умолчанию) | для игры потерянная атака дороже, чем короткая потеря пакетов у отдельного игрока: клиент переживает потери (повторы, тайм-ауты из ПР№3) |
| Исключение | правила UDP flood → **Medium**, если в Analytics видны срабатывания на легитимных игроков в пик (начало матча, массовое переподключение после сбоя) | сначала смотреть логи срабатываний в *Security → Events*, понижать только конкретное правило, а не весь набор |

Дополнительно на стороне origin (своя защита, не зависит от Cloudflare):

* пакеты без заголовка SPP отбрасываются (`DROP: нет заголовка Simple Proxy Protocol`);
* пакеты с `payloadSize`, не совпадающим с размером датаграммы, отбрасываются;
* рукопожатие отвечает пакетом того же размера, что и запрос (32 байта + 5 байт заголовка, с подписью — 96):
  сервер нельзя использовать как усилитель (amplification) для атак на чужие адреса;
* число сессий ограничено (`--max-sessions`).

**[скриншот: страница Network-layer DDoS Attack Protection с выбранной чувствительностью]**

## 4. Что сервер видит в адресе отправителя

Без Simple Proxy Protocol `recvfrom` возвращает адрес узла Cloudflare. Все игроки выглядят как
несколько адресов Cloudflare, и бан по IP заблокировал бы невиновных. С SPP Cloudflare добавляет
к каждой датаграмме заголовок (38 байт, сетевой порядок байт):

| Байты | Поле |
|---|---|
| 0–1 | magic `0x56EC` |
| 2–17 | адрес клиента (IPv6 или IPv4-mapped IPv6) |
| 18–33 | адрес прокси |
| 34–35 | порт клиента |
| 36–37 | порт прокси |

Сервер ([`server_main.cpp`](../src/server_main.cpp), флаг `--spp`) разбирает заголовок, ведёт таблицу
сессий по реальному адресу клиента и добавляет тот же заголовок к ответам, как требует Cloudflare.
[Формат заголовка](https://developers.cloudflare.com/spectrum/reference/simple-proxy-protocol-header/).

## 5. Результат теста подключения через прокси (эмуляция)

Без Enterprise-аккаунта Spectrum заменён эмулятором [`tools/spectrum_emulator.py`](../tools/spectrum_emulator.py).
Как и Spectrum, он принимает датаграммы клиента и пересылает их на origin **со своего адреса**,
а с `--spp` добавляет заголовок SPP и снимает его с ответов. Стенд — три контейнера в одной
docker-сети ([`tools/spectrum_demo.sh`](../tools/spectrum_demo.sh)):

| Узел | Адрес | Роль |
|---|---|---|
| клиент | 172.30.0.30 | `udp_client game.example.com:9000` |
| «Cloudflare» | 172.30.0.20, DNS-имя `game.example.com` | `spectrum_emulator.py` |
| origin | 172.30.0.10 | `udp_server` |

Полный лог: [`evidence/10_spectrum.txt`](evidence/10_spectrum.txt).

**Без SPP** — origin видит только адрес прокси:

```
Server on UDP 9000
[172.30.0.20:52163] session established
[172.30.0.20:52163] LOGIN seq=1 user=dima (пароль получен, 11 симв.)
```

Клиент при этом может подключиться и напрямую к `172.30.0.10:9000` (мимо прокси). В реальной
схеме это закрывает файрвол.

**С SPP** — origin видит реального клиента, а пакеты в обход прокси отбрасывает:

```
Server on UDP 9000 (Spectrum SPP)
[172.30.0.30:58543 via 172.30.0.20:54085] session established
[172.30.0.30:58543 via 172.30.0.20:54085] LOGIN seq=1 user=dima (пароль получен, 11 симв.)
[172.30.0.30:58543 via 172.30.0.20:54085] MOVEMENT seq=2 (0.0, 0.0, 0.0)
[172.30.0.30:47473] DROP: нет заголовка Simple Proxy Protocol      ← прямое подключение к origin
```

Клиент через `game.example.com:9000`: рукопожатие и 4 из 4 зашифрованных ответов; напрямую к origin —
`handshake failed`.

> Проверка отсутствия SPP — не замена файрволу: атакующий, который может достучаться до origin,
> подделает заголовок SPP с чужим адресом. Защиту даёт только правило «UDP 9000 — только от Cloudflare».

## 6. Чек-лист для сдачи с настоящим Spectrum

- [ ] скриншот приложения Spectrum (UDP, 9000 → IP VM:9000, Simple Proxy Protocol);
- [ ] скриншот DNS: только запись Spectrum, без A-записи с IP VM;
- [ ] скриншот Network-layer DDoS ruleset с выбранной чувствительностью;
- [ ] `sudo ufw status numbered` на VM;
- [ ] лог сервера с `[IP игрока via IP Cloudflare]` после подключения клиента к `game.example.com:9000`;
- [ ] попытка подключиться напрямую к IP VM — тайм-аут.
