#!/usr/bin/env python3
"""Локальная эмуляция Cloudflare Spectrum (UDP) для проверки origin-сервера без Enterprise-аккаунта.

Как и Spectrum, прокси принимает датаграммы клиентов на своём адресе и пересылает их на origin
со СВОЕГО адреса (отдельный порт на каждого клиента). Поэтому без Simple Proxy Protocol origin
видит только адрес прокси. С --spp к каждой датаграмме добавляется 38-байтный заголовок SPP
с реальным адресом клиента, а из ответов origin он проверяется и снимается.

  python3 spectrum_emulator.py --listen 9000 --origin 172.30.0.10:9000 [--spp]
"""
import argparse
import ipaddress
import select
import socket
import struct

SPP_MAGIC, SPP_SIZE = 0x56EC, 38


def mapped(ip):
    """IPv4 -> IPv4-mapped IPv6 (16 байт), как в заголовке SPP."""
    return ipaddress.IPv6Address("::ffff:" + ip).packed


def spp_header(client, proxy):
    return struct.pack("!H16s16sHH", SPP_MAGIC, mapped(client[0]), mapped(proxy[0]), client[1], proxy[1])


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--listen", type=int, default=9000)
    ap.add_argument("--origin", required=True)
    ap.add_argument("--spp", action="store_true", help="добавлять Simple Proxy Protocol")
    ap.add_argument("--public-ip", help="адрес прокси для поля proxy address (по умолчанию — адрес интерфейса)")
    args = ap.parse_args()

    host, port = args.origin.rsplit(":", 1)
    origin = (socket.gethostbyname(host), int(port))
    front = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    front.bind(("0.0.0.0", args.listen))
    public_ip = args.public_ip or socket.gethostbyname(socket.gethostname())
    upstream = {}   # адрес клиента -> сокет к origin
    owner = {}      # сокет -> адрес клиента
    print(f"spectrum-emulator {public_ip}:{args.listen} -> origin {origin[0]}:{origin[1]}, SPP={'on' if args.spp else 'off'}",
          flush=True)

    while True:
        ready, _, _ = select.select([front, *owner], [], [])
        for sock in ready:
            if sock is front:
                data, client = front.recvfrom(2048)
                if client not in upstream:
                    up = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                    up.connect(origin)
                    upstream[client], owner[up] = up, client
                    print(f"new client {client[0]}:{client[1]} -> origin from {up.getsockname()[0]}:{up.getsockname()[1]}",
                          flush=True)
                if args.spp:
                    data = spp_header(client, (public_ip, args.listen)) + data
                upstream[client].send(data)
            else:
                data = sock.recv(2048)
                client = owner[sock]
                if args.spp:
                    if len(data) < SPP_SIZE or struct.unpack_from("!H", data)[0] != SPP_MAGIC:
                        print("DROP: ответ origin без заголовка SPP", flush=True)
                        continue
                    data = data[SPP_SIZE:]
                front.sendto(data, client)


if __name__ == "__main__":
    main()
