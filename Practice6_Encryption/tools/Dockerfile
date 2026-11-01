# Среда сборки и захвата трафика для ПР№6 (Linux, как на origin-VM).
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        g++ cmake make libssl-dev tcpdump tshark python3 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /work
# для MITM-прокси (X25519/AES-GCM на Python)
RUN apt-get update && apt-get install -y --no-install-recommends python3-cryptography \
    && rm -rf /var/lib/apt/lists/*
