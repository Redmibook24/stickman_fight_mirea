# Runtime-образ выделенного сервера ПР№7.
#
# В образ попадает только готовый серверный Linux-билд Unity (Builds/LinuxServer), а не редактор
# и не исходники: билд собирается заранее скриптом deploy/build_server.ps1. Поэтому образ
# небольшой и не содержит ничего, кроме того, что нужно для запуска сервера.
#
# Сборка из каталога Practice7_Deploy (контекст сборки ограничен файлом .dockerignore):
#   docker build -f deploy/Dockerfile -t stickman-server:pr7 .

# Базовый образ закреплён по хэшу: тег 22.04 со временем указывает на новые сборки, а хэш нет.
# Ubuntu 22.04 (glibc 2.35) совместима с Linux-плеером Unity 6.
ARG BASE_IMAGE=ubuntu:22.04@sha256:f64b2ffc2f10ab8f8505aa56b2664bd846cbb9df87c7e2532e30f7667021c0e6

# ── Этап 1: библиотека steamclient.so для Steam GameServer ──
# Выделенный сервер работает с Steam без Steam-клиента, но ему нужна steamclient.so из пакета
# «Steamworks SDK Redist» (app 1007). Её раздаёт сама Valve через официальный steamcmd, поэтому
# она скачивается в отдельном этапе, а в итоговый образ копируется только сам файл.
# Версию app 1007 Valve не позволяет закрепить: при каждой сборке берётся актуальная.
FROM ${BASE_IMAGE} AS steam-redist
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates=20260816ubuntu0.22.04.2 \
        curl=7.81.0-1ubuntu1.30 \
        lib32gcc-s1=12.3.0-1ubuntu1~22.04.3 \
        lib32stdc++6=12.3.0-1ubuntu1~22.04.3 \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /steamcmd \
    && curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar -xz -C /steamcmd \
    && /steamcmd/steamcmd.sh +force_install_dir /redist +login anonymous +app_update 1007 validate +quit \
    && test -f /redist/linux64/steamclient.so

# ── Этап 2: итоговый образ сервера ──
FROM ${BASE_IMAGE}

# Версии пакетов закреплены, чтобы образ собирался одинаково и через месяц.
#   ca-certificates - HTTPS-сертификаты (связь с серверами Steam);
#   tini            - init-процесс PID 1: пересылает серверу SIGTERM от `docker stop`
#                     и забирает завершившиеся процессы.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates=20260816ubuntu0.22.04.2 \
        tini=0.19.0-1 \
    && rm -rf /var/lib/apt/lists/*

# Сервер работает от непривилегированного пользователя: даже при уязвимости в игре
# атакующий не получит root внутри контейнера.
RUN groupadd --system --gid 10001 game \
    && useradd --system --uid 10001 --gid game --home-dir /srv/game --create-home --shell /usr/sbin/nologin game

WORKDIR /srv/game
COPY --chown=game:game Builds/LinuxServer/ ./
# Steam GameServer ищет steamclient.so в ~/.steam/sdk64 пользователя, от которого запущен сервер.
COPY --from=steam-redist --chown=game:game /redist/linux64/steamclient.so /srv/game/.steam/sdk64/steamclient.so
COPY --chown=game:game --chmod=0755 deploy/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod 0755 StickmanServer.x86_64

USER game

# Параметры сервера по умолчанию, переопределяются в compose.yaml или через `docker run -e`.
ENV GAME_PORT=7777 \
    SERVER_NAME="Stickman Fight PR7" \
    MAX_PLAYERS=4 \
    NET_PROFILE=ip

# EXPOSE только документирует порты, публикует их отображение `-p ...` при запуске (compose.yaml).
#   7777/udp  - игровой трафик (UnityTransport в IP-профиле, Steam Sockets в Steam-профиле);
#   27015/udp - ответы на запросы списка серверов Steam (только Steam-профиль).
EXPOSE 7777/udp 27015/udp

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint.sh"]
