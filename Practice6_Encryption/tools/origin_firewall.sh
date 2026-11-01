#!/usr/bin/env bash
# Файрвол origin-VM: игровой UDP-порт открыт только для адресов Cloudflare, SSH — только для админа.
# Иначе атакующий, узнавший реальный IP сервера, обойдёт Spectrum и ударит по origin напрямую.
#
#   sudo ADMIN_IP=203.0.113.7 GAME_PORT=9000 bash origin_firewall.sh
#
# Диапазоны берутся с официальных страниц https://www.cloudflare.com/ips-v4 и /ips-v6.
# Их стоит перечитывать по расписанию (cron), Cloudflare изредка их меняет.
set -euo pipefail
GAME_PORT="${GAME_PORT:-9000}"
ADMIN_IP="${ADMIN_IP:?укажите ADMIN_IP — адрес, с которого разрешён SSH}"

CF4=$(curl -fsS https://www.cloudflare.com/ips-v4)
CF6=$(curl -fsS https://www.cloudflare.com/ips-v6)

ufw --force reset
ufw default deny incoming
ufw default allow outgoing
ufw allow from "$ADMIN_IP" to any port 22 proto tcp comment 'SSH admin'
for net in $CF4 $CF6; do
    ufw allow from "$net" to any port "$GAME_PORT" proto udp comment 'Cloudflare Spectrum'
done
ufw --force enable
ufw status numbered
