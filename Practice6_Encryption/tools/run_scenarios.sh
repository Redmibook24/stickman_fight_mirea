#!/usr/bin/env bash
# Все проверки ПР№6 на 127.0.0.1 внутри контейнера (Linux, root — нужен для tcpdump на loopback):
#   docker build -t pr6-env -f tools/Dockerfile tools
#   docker run --rm -v "$PWD:/work" pr6-env bash tools/run_scenarios.sh
# Результаты: docs/captures/*.pcap (открываются в Wireshark) и docs/evidence/*.txt (логи).
set -u
cd /work
B=/tmp/build
CAP=docs/captures
EV=docs/evidence
mkdir -p "$CAP" "$EV"
cmake -S . -B $B -DCMAKE_BUILD_TYPE=Release >/dev/null && cmake --build $B -j4 >/dev/null || exit 1

SERVER_PID=; PROXY_PID=; DUMP_PID=
start_server() { stdbuf -oL $B/udp_server "$@" > /tmp/server.log 2>&1 & SERVER_PID=$!; sleep 0.4; }
start_proxy()  { python3 -u tools/udp_mitm.py --listen 9001 --server 127.0.0.1:9000 "$@" > /tmp/proxy.log 2>&1 & PROXY_PID=$!; sleep 0.6; }
start_dump()   { tcpdump -i lo -U -w "$1" udp port 9000 >/dev/null 2>&1 & DUMP_PID=$!; sleep 1; }
stop_all() {
    sleep 0.4
    [ -n "$DUMP_PID" ] && kill -INT $DUMP_PID 2>/dev/null && wait $DUMP_PID 2>/dev/null
    [ -n "$PROXY_PID" ] && kill $PROXY_PID 2>/dev/null
    [ -n "$SERVER_PID" ] && kill $SERVER_PID 2>/dev/null
    wait 2>/dev/null
    SERVER_PID=; PROXY_PID=; DUMP_PID=
}
section() { # имя файла, заголовок; затем — логи клиента, сервера, прокси
    local out="$EV/$1.txt"
    {
        echo "# $2"
        echo; echo "## Клиент"; cat /tmp/client.log
        echo; echo "## Сервер"; cat /tmp/server.log
        [ -s /tmp/proxy.log ] && { echo; echo "## Прокси"; cat /tmp/proxy.log; }
    } > "$out"
    : > /tmp/proxy.log
    echo "== $2 -> $out"
}
dump_text() { # pcap -> текстовый вид, как в Wireshark (список пакетов + байты)
    {
        echo "# tshark -r $1"
        tshark -r "$1" 2>/dev/null
        echo
        echo "# tshark -r $1 -x  (байты каждого пакета, как панель Packet Bytes в Wireshark)"
        tshark -r "$1" -x 2>/dev/null
    } > "$2"
}

# 1. До шифрования: открытые LOGIN / MOVEMENT / SHOOT
start_dump $CAP/before_plain.pcap
start_server --allow-plain
$B/udp_client 127.0.0.1 --plain --count 4 > /tmp/client.log 2>&1
stop_all
section 01_plain "До шифрования (--plain)"
dump_text $CAP/before_plain.pcap $EV/01_plain_wireshark.txt

# 2. После шифрования
start_dump $CAP/after_encrypted.pcap
start_server
$B/udp_client 127.0.0.1 --count 4 > /tmp/client.log 2>&1
stop_all
section 02_encrypted "После шифрования (X25519 + AES-256-GCM)"
dump_text $CAP/after_encrypted.pcap $EV/02_encrypted_wireshark.txt

# 3. Открытый пакет на сервер, который требует шифрования
start_server
$B/udp_client 127.0.0.1 --plain --count 1 > /tmp/client.log 2>&1
stop_all
section 03_plain_rejected "Открытые пакеты отклоняются сервером без --allow-plain"

# 4. Подмена одного байта шифртекста
start_server; start_proxy --mode tamper --target 3
$B/udp_client 127.0.0.1:9001 --count 4 > /tmp/client.log 2>&1
stop_all
section 04_tamper "Негативный тест: подмена байта шифртекста"

# 5. Повтор пакета (replay)
start_server; start_proxy --mode replay --target 3
$B/udp_client 127.0.0.1:9001 --count 4 > /tmp/client.log 2>&1
sleep 0.8
stop_all
section 05_replay "Негативный тест: повтор пакета (replay)"

# 6. Отражение ответа сервера обратно серверу
start_server; start_proxy --mode reflect
$B/udp_client 127.0.0.1:9001 --count 2 > /tmp/client.log 2>&1
stop_all
section 06_reflect "Негативный тест: отражение ответа сервера (reflection)"

# 7. MITM на голом DH: атака удаётся
start_server; start_proxy --mode dh-mitm
$B/udp_client 127.0.0.1:9001 --count 2 > /tmp/client.log 2>&1
stop_all
section 07_dh_mitm "Ограничение: MITM на неаутентифицированном Диффи–Хеллмане"

# 8. MITM при подписанном рукопожатии и закреплённом ключе: атака обнаружена
rm -f /tmp/server_ed25519.pem
start_server --sign-key /tmp/server_ed25519.pem
PIN=$(grep -o '[0-9a-f]\{64\}' /tmp/server.log | head -1)
start_proxy --mode dh-mitm
$B/udp_client 127.0.0.1:9001 --count 2 --pin "$PIN" > /tmp/client.log 2>&1
stop_all
section 08_dh_mitm_signed "Защита: подпись рукопожатия Ed25519 + pin, MITM обнаружен"

# 9. Подписанное рукопожатие без атакующего: работает
start_server --sign-key /tmp/server_ed25519.pem
$B/udp_client 127.0.0.1 --count 2 --pin "$PIN" > /tmp/client.log 2>&1
stop_all
section 09_signed_ok "Подписанное рукопожатие без атакующего"

echo "== crypto_test" ; (cd /tmp && $B/crypto_test) | tee $EV/00_crypto_test.txt
