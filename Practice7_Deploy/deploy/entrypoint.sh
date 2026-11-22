#!/bin/sh
# Точка входа контейнера: запускает серверный билд с параметрами из переменных окружения.
# exec заменяет shell процессом сервера, поэтому SIGTERM от `docker stop` (через tini)
# приходит прямо в сервер, и тот успевает завершить сессию и закрыть соединения.
set -eu

echo "[entrypoint] Stickman Fight dedicated server: profile=${NET_PROFILE}, port=${GAME_PORT}/udp, max=${MAX_PLAYERS}"

# -batchmode -nographics : без окна и без графики (серверный билд и так их не использует)
# -logFile -             : лог Unity в stdout, чтобы он был виден в `docker logs`
# остальные ключи читает DedicatedServer.cs, лишние аргументы из `docker run ... <args>` передаются дальше
exec ./StickmanServer.x86_64 \
    -batchmode -nographics -logFile - \
    -port "${GAME_PORT}" \
    -serverName "${SERVER_NAME}" \
    -maxPlayers "${MAX_PLAYERS}" \
    -netProfile "${NET_PROFILE}" \
    "$@"
