#!/usr/bin/env python3
"""UDP-прокси «человек посередине» для негативных тестов ПР№6.

Клиент подключается к прокси (--listen), прокси пересылает пакеты серверу (--server).

  pass     только пересылает и печатает пакеты (что видит пассивный наблюдатель)
  tamper   меняет один байт шифртекста в пакете клиента с seq == --target
  replay   повторно отправляет пакет клиента с seq == --target (сразу и через 0,5 с)
  reflect  возвращает серверу его же зашифрованный ответ
  dh-mitm  активная атака на голый Диффи–Хеллман: подменяет открытые ключи, читает
           и меняет трафик (нужен python3-cryptography)

  python3 udp_mitm.py --listen 9001 --server 127.0.0.1:9000 --mode tamper --target 3
"""
import argparse
import select
import socket
import struct
import time

HEADER = struct.Struct("<BHH")  # type, seq, payloadSize — как struct Header в protocol.h
KEY_EXCHANGE, ENCRYPTED = 10, 11
NAMES = {1: "MOVEMENT", 2: "SHOOT", 3: "ACK", 4: "LOGIN", 10: "KEY_EXCHANGE", 11: "ENCRYPTED"}
NONCE, TAG = 12, 16


def describe(direction, data):
    """Строка лога о пакете: направление, тип, seq, размер и первые 24 байта тела в hex.

    Так выглядит трафик для пассивного наблюдателя: заголовок читается, тело ENCRYPTED это шум.
    """
    if len(data) < HEADER.size:
        return f"{direction} {len(data)} B (short)"
    t, seq, size = HEADER.unpack_from(data)
    body = data[HEADER.size:]
    return f"{direction} {NAMES.get(t, t):<12} seq={seq:<3} len={size:<3} body={body[:24].hex(' ')}{' …' if len(body) > 24 else ''}"


class DhMitm:
    """Подмена ключей X25519: у клиента и у сервера свои ключи сессии, прокси знает оба.

    Схема атаки на голый Диффи-Хеллман:
      клиент  --ключ клиента-->  прокси  --ключ атакующего to_server-->  сервер
      клиент  <--ключ атакующего to_client--  прокси  <--ключ сервера--  сервер
    Клиент думает, что общается с сервером, а на деле выводит ключ сессии с атакующим
    (k_client); сервер так же выводит ключ с атакующим (k_server). Прокси расшифровывает
    пакет одним ключом и зашифровывает другим, поэтому теги у обеих сторон сходятся.
    Против подписи Ed25519 атака не работает: подпись сервера покрывает настоящие ключи.
    """

    def __init__(self):
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        from cryptography.hazmat.primitives.kdf.hkdf import HKDF
        from cryptography.hazmat.primitives import hashes, serialization
        self.X25519PrivateKey, self.X25519PublicKey, self.AESGCM = X25519PrivateKey, X25519PublicKey, AESGCM
        self.HKDF, self.hashes, self.ser = HKDF, hashes, serialization
        self.to_client = X25519PrivateKey.generate()   # ключ, который получит клиент вместо ключа сервера
        self.to_server = X25519PrivateKey.generate()   # ключ, который получит сервер вместо ключа клиента
        self.client_pub = None
        self.k_client = self.k_server = None

    def raw(self, priv):
        """Открытый ключ X25519 сырыми 32 байтами, как в пакете KEY_EXCHANGE."""
        return priv.public_key().public_bytes(self.ser.Encoding.Raw, self.ser.PublicFormat.Raw)

    def derive(self, priv, peer_pub):
        """Ключ сессии из своего закрытого и чужого открытого ключа, ровно как в crypto.cpp."""
        secret = priv.exchange(self.X25519PublicKey.from_public_bytes(peer_pub))
        # то же, что DeriveSessionKey: HKDF-SHA256 без соли, info = "game-udp-v1"
        return self.HKDF(algorithm=self.hashes.SHA256(), length=32, salt=None, info=b"game-udp-v1").derive(secret)

    def client_hello(self, data):
        """KEY_EXCHANGE клиента: запоминаем его ключ, а серверу отдаём ключ атакующего."""
        self.client_pub = data[HEADER.size:HEADER.size + 32]
        print("  [MITM] перехвачен открытый ключ клиента, серверу уходит ключ атакующего")
        return data[:HEADER.size] + self.raw(self.to_server)

    def server_hello(self, data):
        """KEY_EXCHANGE сервера: выводим оба ключа сессии, клиенту отдаём ключ атакующего.

        Подпись сервера (если есть) пересылается как есть: подделать её нельзя, поэтому
        клиент с --pin увидит несовпадение и оборвёт рукопожатие.
        """
        body = data[HEADER.size:]
        server_pub, signature = body[:32], body[32:]
        self.k_server = self.derive(self.to_server, server_pub)
        self.k_client = self.derive(self.to_client, self.client_pub)
        print("  [MITM] две сессии: клиент↔атакующий и атакующий↔сервер, оба ключа известны")
        if signature:
            print("  [MITM] сервер подписал рукопожатие — подпись не подойдёт к подменённому ключу")
        return data[:HEADER.size] + self.raw(self.to_client) + signature

    def recrypt(self, data, from_client):
        """Расшифровать пакет ключом одной стороны и зашифровать ключом другой.

        По пути читаем LOGIN (пароль) и меняем координаты MOVEMENT на 999.0, чтобы показать,
        что при MITM атакующий не только читает, но и незаметно подменяет команды.
        Заголовок и nonce сохраняются, тег вычисляется заново, поэтому стороны ничего не замечают.
        """
        header, body = data[:HEADER.size], data[HEADER.size:]
        nonce, ct = body[:NONCE], body[NONCE:]
        src, dst = (self.k_client, self.k_server) if from_client else (self.k_server, self.k_client)
        plain = bytearray(self.AESGCM(src).decrypt(nonce, ct, header))
        kind = NAMES.get(plain[0], plain[0])
        if from_client and plain[0] == 4:
            user, pwd = (s.rstrip(b"\0").decode() for s in struct.unpack_from("16s16s", plain, 1))
            print(f"  [MITM] расшифрован LOGIN: user={user} password={pwd}")
        elif from_client and plain[0] == 1:
            x, y, z = struct.unpack_from("<fff", plain, 1)
            struct.pack_into("<fff", plain, 1, 999.0, y, z)
            print(f"  [MITM] расшифрован MOVEMENT ({x:.1f}, {y:.1f}, {z:.1f}) → подменён на (999.0, {y:.1f}, {z:.1f})")
        else:
            print(f"  [MITM] расшифрован {kind}: {bytes(plain).hex(' ')}")
        return header + nonce + self.AESGCM(dst).encrypt(nonce, bytes(plain), header)


def main():
    """Цикл прокси: front принимает пакеты клиента, back общается с сервером.

    select ждёт данные на обоих сокетах сразу, а тайм-аут нужен для отложенного повтора
    в режиме replay (пакет отправляется второй раз через 0,5 с).
    """
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--listen", type=int, default=9001)
    ap.add_argument("--server", default="127.0.0.1:9000")
    ap.add_argument("--mode", choices=["pass", "tamper", "replay", "reflect", "dh-mitm"], default="pass")
    ap.add_argument("--target", type=int, default=3, help="seq пакета клиента для tamper/replay")
    args = ap.parse_args()

    host, port = args.server.rsplit(":", 1)
    server = (host, int(port))
    front = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    front.bind(("0.0.0.0", args.listen))
    back = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    back.connect(server)
    mitm = DhMitm() if args.mode == "dh-mitm" else None
    client = None
    delayed = []  # (время, пакет) для отложенного повтора
    print(f"proxy :{args.listen} -> {args.server}, mode={args.mode}", flush=True)

    while True:
        timeout = max(0.0, delayed[0][0] - time.time()) if delayed else None
        ready, _, _ = select.select([front, back], [], [], timeout)
        while delayed and delayed[0][0] <= time.time():
            back.send(delayed.pop(0)[1])
            print("  [REPLAY] отправлен повтор через 0,5 с", flush=True)

        if front in ready:
            data, client = front.recvfrom(2048)
            print(describe("C->S", data), flush=True)
            t, seq = (HEADER.unpack_from(data)[:2]) if len(data) >= HEADER.size else (None, None)
            if mitm and t == KEY_EXCHANGE:
                data = mitm.client_hello(data)
            elif mitm and t == ENCRYPTED:
                data = mitm.recrypt(data, from_client=True)
            elif args.mode == "tamper" and t == ENCRYPTED and seq == args.target:
                # второй байт шифртекста: сразу после заголовка и nonce
                pos = HEADER.size + NONCE + 1
                data = data[:pos] + bytes([data[pos] ^ 0x01]) + data[pos + 1:]
                print(f"  [TAMPER] байт шифртекста #{pos} изменён (xor 0x01)", flush=True)
            back.send(data)
            if args.mode == "replay" and t == ENCRYPTED and seq == args.target:
                # два повтора: сразу (проверка «тот же счётчик») и через 0,5 с, когда окно
                # сервера уже ушло вперёд (проверка «опоздавший повтор»)
                back.send(data)
                delayed.append((time.time() + 0.5, data))
                print("  [REPLAY] пакет отправлен повторно", flush=True)

        if back in ready:
            data = back.recv(2048)
            print(describe("S->C", data), flush=True)
            t = data[0] if data else None
            if mitm and t == KEY_EXCHANGE:
                data = mitm.server_hello(data)
            elif mitm and t == ENCRYPTED:
                data = mitm.recrypt(data, from_client=False)
            if args.mode == "reflect" and t == ENCRYPTED:
                # ответ сервера зашифрован тем же ключом сессии, поэтому без проверки
                # направления сервер принял бы его как команду клиента
                back.send(data)
                print("  [REFLECT] ответ сервера отправлен обратно серверу", flush=True)
            if client:
                front.sendto(data, client)


if __name__ == "__main__":
    main()
