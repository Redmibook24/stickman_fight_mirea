# Практическая работа №6: шифрование UDP-трафика и защита от DDoS

UDP-клиент и сервер на C++17 + OpenSSL 3:

* обмен ключами **X25519** при первом соединении, вывод ключа **HKDF-SHA256**;
* шифрование пакетов **AES-256-GCM**, заголовок пакета открыт, но аутентифицирован (AAD);
* защита от повторов (окно 64 пакета) и от отражения ответов сервера;
* опционально — подпись рукопожатия **Ed25519** и закреплённый ключ сервера в клиенте (защита от MITM);
* поддержка **Cloudflare Spectrum** (Simple Proxy Protocol), подключение по доменному имени.

Документы:

* [docs/Security.md](docs/Security.md) — модель угроз, Wireshark «до/после», схема обмена ключами,
  алгоритмы, формат пакета, тесты, ограничения, ответы на контрольные вопросы;
* [docs/Cloudflare_Spectrum_Setup.md](docs/Cloudflare_Spectrum_Setup.md) — настройка Spectrum, файрвол
  origin, правила DDoS-защиты, результат теста через прокси;
* [docs/captures/](docs/captures) — записи трафика (`.pcap`, открываются в Wireshark);
* [docs/evidence/](docs/evidence) — логи всех проверок.

## Сборка

Linux / WSL (`sudo apt install g++ cmake libssl-dev`):

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
./build/crypto_test
```

Или в контейнере, где есть ещё tcpdump и tshark:

```bash
docker build -t pr6-env -f tools/Dockerfile tools
docker run --rm -v "$PWD:/work" pr6-env bash tools/run_scenarios.sh   # все проверки + pcap
bash tools/spectrum_demo.sh                                            # схема клиент → Spectrum → origin
```

## Запуск

```bash
./build/udp_server                                     # порт 9000
./build/udp_client 127.0.0.1                           # рукопожатие, LOGIN, MOVEMENT, SHOOT
```

| Ключ | Где | Что делает |
|---|---|---|
| `--plain` | клиент | без шифрования — для снимка трафика «до» |
| `--allow-plain` | сервер | принимать открытые пакеты (только для снимка «до») |
| `--sign-key file.pem` | сервер | подписывать рукопожатие Ed25519 (ключ создаётся при первом запуске, pin печатается в лог) |
| `--pin <hex>` | клиент | принимать только рукопожатие, подписанное этим ключом |
| `--spp` | сервер | работа за Cloudflare Spectrum: разбор Simple Proxy Protocol, реальный IP клиента |
| `host:port` | клиент | адрес сервера, можно доменом: `game.example.com:9000` |
| `--count N` | клиент | число пакетов движения |

## Структура

```
src/protocol.h         заголовок пакета, типы, полезные нагрузки, константы SPP
src/crypto.h/.cpp      X25519, HKDF, AES-256-GCM, окно повторов, подпись Ed25519
src/server_main.cpp    сервер: сессии по адресу, проверки, SPP
src/client_main.cpp    клиент: рукопожатие с повторами, шифрованные команды
src/crypto_test.cpp    24 модульные проверки
tools/udp_mitm.py      MITM-прокси: pass / tamper / replay / reflect / dh-mitm
tools/spectrum_emulator.py   локальный эмулятор Spectrum (UDP + Simple Proxy Protocol)
tools/run_scenarios.sh       все проверки и запись pcap
tools/spectrum_demo.sh       стенд из трёх контейнеров
tools/origin_firewall.sh     ufw: UDP 9000 только от Cloudflare, SSH только от админа
tools/render_capture.py      картинки «до/после» из вывода tshark
```
