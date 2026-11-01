#!/usr/bin/env bash
# Схема «клиент → Spectrum → origin» в трёх контейнерах одной docker-сети:
#   172.30.0.30 клиент  →  172.30.0.20 game.example.com (эмулятор Spectrum)  →  172.30.0.10 origin
# Запуск с хоста из папки Practice6_Encryption (нужен образ pr6-env):
#   bash tools/spectrum_demo.sh
# Результат: docs/evidence/10_spectrum.txt
set -u
WORK="${WORK:-$(pwd -W 2>/dev/null || pwd)}"
NET=pr6net
OUT=docs/evidence/10_spectrum.txt
export MSYS_NO_PATHCONV=1
run() { docker run --rm --network $NET -v "$WORK:/work" pr6-env "$@"; }

docker rm -f pr6-origin pr6-spectrum >/dev/null 2>&1
docker network rm $NET >/dev/null 2>&1
docker network create --subnet 172.30.0.0/24 $NET >/dev/null
docker run --rm -v "$WORK:/work" pr6-env bash -c \
    "cmake -S . -B build -DCMAKE_BUILD_TYPE=Release >/dev/null && cmake --build build -j4 >/dev/null" || exit 1

scenario() { # заголовок, флаги сервера, флаги эмулятора
    docker run -d --name pr6-origin --network $NET --ip 172.30.0.10 -v "$WORK:/work" pr6-env \
        stdbuf -oL build/udp_server $2 >/dev/null
    docker run -d --name pr6-spectrum --network $NET --ip 172.30.0.20 --network-alias game.example.com \
        -v "$WORK:/work" pr6-env python3 -u tools/spectrum_emulator.py --listen 9000 \
        --origin 172.30.0.10:9000 --public-ip 172.30.0.20 $3 >/dev/null
    sleep 1.5
    {
        echo "# $1"
        echo; echo "## Клиент (172.30.0.30) → game.example.com:9000"
        docker run --rm --network $NET --ip 172.30.0.30 -v "$WORK:/work" pr6-env build/udp_client game.example.com:9000 --count 2
        echo; echo "## Прямое подключение к origin мимо прокси (172.30.0.30 → 172.30.0.10:9000)"
        docker run --rm --network $NET --ip 172.30.0.30 -v "$WORK:/work" pr6-env build/udp_client 172.30.0.10:9000 --count 1
        sleep 0.5
        echo; echo "## Прокси (эмулятор Spectrum)"; docker logs pr6-spectrum 2>&1
        echo; echo "## Origin"; docker logs pr6-origin 2>&1
        echo
    } >> "$OUT"
    docker rm -f pr6-origin pr6-spectrum >/dev/null
}

: > "$OUT"
scenario "Без Simple Proxy Protocol: origin видит только адрес прокси" "" ""
scenario "С Simple Proxy Protocol: origin видит реальный адрес клиента и отбрасывает пакеты мимо прокси" "--spp" "--spp"
docker network rm $NET >/dev/null
cat "$OUT"
