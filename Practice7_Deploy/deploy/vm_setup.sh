#!/usr/bin/env bash
# Однократная подготовка Linux VM (Ubuntu 22.04/24.04) под выделенный сервер ПР№7.
# Запускается на VM от пользователя с sudo:
#   ADMIN_IP=<ваш_внешний_IP> bash vm_setup.sh
#
# Что делает:
#   1. ставит Docker Engine и плагин compose из официального репозитория Docker;
#   2. настраивает ufw: всё входящее запрещено, SSH только с ADMIN_IP, игровой UDP-порт открыт.
#
# Важно про Docker и ufw: порт, опубликованный контейнером (-p 7777:7777/udp), Docker открывает
# своими правилами iptables в обход ufw. Поэтому настоящая граница доступа к игровому порту -
# правила группы безопасности облака (security group), а ufw здесь защищает SSH и прочие
# службы самой VM. Проверка доступности делается игровым подключением извне, а не по ufw.
set -euo pipefail

ADMIN_IP="${ADMIN_IP:?Укажите ADMIN_IP=<внешний IP администратора> для доступа по SSH}"
GAME_PORT="${GAME_PORT:-7777}"

echo "== 1. Docker Engine"
if ! command -v docker >/dev/null; then
    sudo apt-get update
    sudo apt-get install -y ca-certificates curl
    sudo install -m 0755 -d /etc/apt/keyrings
    sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    sudo chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
        | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
    sudo apt-get update
    sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
fi
# Пользователь может управлять Docker без sudo (вступит в силу после повторного входа по SSH).
sudo usermod -aG docker "$USER"
sudo systemctl enable --now docker
docker --version || sudo docker --version

echo "== 2. Межсетевой экран ufw"
sudo apt-get install -y ufw
sudo ufw --force reset
sudo ufw default deny incoming
sudo ufw default allow outgoing
# SSH только с административного адреса: подбор пароля и сканеры с других адресов не пройдут.
sudo ufw allow from "$ADMIN_IP" to any port 22 proto tcp comment 'SSH admin'
# Игровой порт: UDP, открыт для всех, иначе игроки из внешней сети не подключатся.
sudo ufw allow "$GAME_PORT"/udp comment 'Stickman dedicated server'
sudo ufw --force enable
sudo ufw status numbered

echo
echo "Готово. Перезайдите по SSH, чтобы группа docker применилась."
echo "В панели облака откройте в группе безопасности: TCP 22 с $ADMIN_IP и UDP $GAME_PORT для всех."
