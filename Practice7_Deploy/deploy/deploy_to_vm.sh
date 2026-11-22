#!/usr/bin/env bash
# Доставка собранного образа на VM и запуск сервера. Запускается на машине разработчика
# (Git Bash или Linux) из каталога Practice7_Deploy после deploy/build_server.ps1:
#   VM=user@203.0.113.10 bash deploy/deploy_to_vm.sh
#   VM=user@127.0.0.1 SSH_PORT=2222 SSH_KEY=~/.ssh/vm_key PROFILE=steam VBOX=1 bash deploy/deploy_to_vm.sh
#
# Переменные: VM (обязательно), SSH_PORT (22), SSH_KEY (ключ по умолчанию), PROFILE (ip | steam),
# VBOX=1 для VM в VirtualBox на Windows с Hyper-V (см. compose.vbox.yaml).
#
# Образ собирается локально и передаётся на VM архивом через SSH (docker save | docker load):
# на VM не нужны ни Unity, ни исходники, ни доступ к реестру образов.
set -euo pipefail

VM="${VM:?Укажите VM=user@host}"
SSH_PORT="${SSH_PORT:-22}"
PROFILE="${PROFILE:-ip}"
IMAGE="stickman-server:pr7"
REMOTE_DIR="stickman-pr7"
export MSYS_NO_PATHCONV=1   # Git Bash: не превращать удалённые пути в пути Windows

SSH_OPTS=(-p "$SSH_PORT")
SCP_OPTS=(-P "$SSH_PORT")
if [ -n "${SSH_KEY:-}" ]; then
    SSH_OPTS+=(-i "$SSH_KEY")
    SCP_OPTS+=(-i "$SSH_KEY")
fi

COMPOSE="-f deploy/compose.yaml"
FILES=(deploy/compose.yaml)
case "$PROFILE" in
    ip) ;;
    steam) COMPOSE="$COMPOSE -f deploy/compose.steam.yaml"; FILES+=(deploy/compose.steam.yaml) ;;
    *) echo "PROFILE должен быть ip или steam" >&2; exit 1 ;;
esac
# VirtualBox поверх Hyper-V: сервер привязывается к одному ядру (причина в compose.vbox.yaml).
if [ "${VBOX:-0}" = "1" ]; then
    COMPOSE="$COMPOSE -f deploy/compose.vbox.yaml"
    FILES+=(deploy/compose.vbox.yaml)
fi

echo "== 1. Сборка образа"
docker build -f deploy/Dockerfile -t "$IMAGE" .

echo "== 2. Передача образа на $VM"
ssh "${SSH_OPTS[@]}" "$VM" "mkdir -p ~/$REMOTE_DIR/deploy"
docker save "$IMAGE" | gzip | ssh "${SSH_OPTS[@]}" "$VM" "gunzip | docker load"
scp "${SCP_OPTS[@]}" "${FILES[@]}" "$VM:~/$REMOTE_DIR/deploy/"

echo "== 3. Перезапуск сервера на VM, профиль $PROFILE"
# --no-build: на VM образ уже загружен, собирать его там не из чего.
# --force-recreate: контейнер пересоздаётся, даже если сменился только профиль.
ssh "${SSH_OPTS[@]}" "$VM" "cd ~/$REMOTE_DIR && docker compose $COMPOSE up -d --no-build --force-recreate && sleep 8 && docker compose $COMPOSE logs --tail 40"
