// UDP-клиент ПР№6.
//   udp_client [host[:port]] [--plain] [--pin <hex Ed25519>] [--count 5] [--user dima --password secret]
//   host может быть доменом (game.example.com:9000 при подключении через Cloudflare Spectrum).
//   --plain  без шифрования: открытые LOGIN/MOVEMENT/SHOOT для снимка трафика «до»
//   --pin    открытый ключ сервера: рукопожатие без верной подписи отвергается (защита от MITM)
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <thread>
#include <chrono>
#include <netdb.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <openssl/crypto.h>
#include "protocol.h"
#include "crypto.h"
using namespace crypto;

// Сценарий клиента (функция main): подключиться к серверу, затем
//   --plain: отправить открытые LOGIN, MOVEMENT, SHOOT (снимок трафика «до»);
//   иначе:   рукопожатие X25519 (с проверкой подписи, если задан --pin), затем те же команды
//            в зашифрованном виде; на каждую ждём зашифрованный ACK и проверяем его.

// Параметры командной строки клиента (см. описание флагов в начале файла).
struct Options {
    std::string host = "127.0.0.1";
    std::string port = std::to_string(PORT);
    bool plain = false;
    bool pinned = false;
    uint8_t pin[SIG_PUB_SIZE];
    int count = 5;
    std::string user = "dima";
    std::string password = "s3cr3t-pass";   // учебный пароль: по нему видно утечку на снимке «до»
};

// Строка из 2*len шестнадцатеричных символов -> len байт. Нужна для --pin (64 символа = 32 байта).
static bool ParseHex(const char* hex, uint8_t* out, size_t len) {
    if (strlen(hex) != len * 2) return false;
    for (size_t i = 0; i < len; ++i)
        if (sscanf(hex + 2 * i, "%2hhx", &out[i]) != 1) return false;
    return true;
}

// Разбор флагов. Аргумент без "--" считается адресом сервера: "host" или "host:port".
static bool ParseArgs(int argc, char** argv, Options& o) {
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--plain") o.plain = true;
        else if (a == "--pin" && i + 1 < argc) {
            if (!ParseHex(argv[++i], o.pin, SIG_PUB_SIZE)) { fprintf(stderr, "--pin: ожидается 64 hex-символа\n"); return false; }
            o.pinned = true;
        }
        else if (a == "--count" && i + 1 < argc) o.count = atoi(argv[++i]);
        else if (a == "--user" && i + 1 < argc) o.user = argv[++i];
        else if (a == "--password" && i + 1 < argc) o.password = argv[++i];
        else if (a.rfind("--", 0) == 0) { fprintf(stderr, "unknown argument: %s\n", argv[i]); return false; }
        else {
            auto colon = a.rfind(':');
            if (colon != std::string::npos) { o.host = a.substr(0, colon); o.port = a.substr(colon + 1); }
            else o.host = a;
        }
    }
    return true;
}

// Сокет, «подключённый» к серверу: recv принимает датаграммы только с его адреса.
// Для UDP connect() не устанавливает соединение, а лишь запоминает адрес: чужие пакеты
// с других адресов ядро отбрасывает само, и они не могут сорвать рукопожатие.
// getaddrinfo разрешает и IP, и доменное имя, поэтому работает адрес game.example.com:9000.
// Тайм-аут приёма 1 с: UDP-пакет может потеряться, и recv не должен ждать вечно.
static int Connect(const Options& o) {
    addrinfo hints{}, *res = nullptr;
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;
    if (getaddrinfo(o.host.c_str(), o.port.c_str(), &hints, &res) != 0 || !res) {
        fprintf(stderr, "cannot resolve %s\n", o.host.c_str());
        return -1;
    }
    int s = socket(res->ai_family, res->ai_socktype, res->ai_protocol);
    char ip[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &((sockaddr_in*)res->ai_addr)->sin_addr, ip, sizeof(ip));
    printf("server %s:%s -> %s\n", o.host.c_str(), o.port.c_str(), ip);
    if (connect(s, res->ai_addr, res->ai_addrlen) < 0) { perror("connect"); freeaddrinfo(res); return -1; }
    freeaddrinfo(res);
    timeval tv{1, 0};
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    return s;
}

// Заполняет структуру LOGIN. strncpy с размером минус 1 и обнулённая структура гарантируют
// завершающий ноль, даже если имя или пароль длиннее 15 символов.
static LoginPayload MakeLogin(const Options& o) {
    LoginPayload l{};
    strncpy(l.user, o.user.c_str(), sizeof(l.user) - 1);
    strncpy(l.password, o.password.c_str(), sizeof(l.password) - 1);
    return l;
}

// ───────── режим «до шифрования» ─────────
// Шлёт команды открытым текстом, чтобы в Wireshark было видно, что утекает без защиты:
// имя, пароль, координаты и тип каждой команды. Сервер принимает их только с --allow-plain.
static int RunPlain(int s, const Options& o) {
    uint16_t seq = 0;
    uint8_t buf[MAX_PACKET];
    auto sendPlain = [&](PacketType t, const void* body, int len) {
        Header h{t, ++seq, (uint16_t)len};
        auto pkt = MakePacket(h, (const uint8_t*)body, len);
        send(s, pkt.data(), pkt.size(), 0);
        int r = recv(s, buf, sizeof(buf), 0);
        printf("seq=%u -> %s\n", seq, r >= (int)sizeof(Header) && ((Header*)buf)->type == PacketType::ACK ? "ACK" : "timeout");
    };
    LoginPayload l = MakeLogin(o);
    sendPlain(PacketType::LOGIN, &l, sizeof(l));
    for (int i = 0; i < o.count; ++i) {
        MovementPayload m{1.0f * i, 0.0f, 2.0f * i};
        sendPlain(PacketType::MOVEMENT, &m, sizeof(m));
        if (i % 2 == 1) {
            ShootPayload sh{1, 0.6f, 0.8f};
            sendPlain(PacketType::SHOOT, &sh, sizeof(sh));
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
    return 0;
}

// ───────── зашифрованный режим ─────────

// Рукопожатие: обмен открытыми ключами X25519 и вывод общего ключа сессии в key.
// Ответ сервера: 32 байта его открытого ключа, а если сервер подписывает рукопожатие,
// ещё 64 байта подписи Ed25519. С --pin клиент принимает ТОЛЬКО ответ с верной подписью:
// это и есть защита от MITM, без неё клиент честно предупреждает, что сервер не проверен.
// Возвращает false, если сервер не ответил за 3 попытки или подпись не сошлась.
static bool Handshake(int s, const Options& o, uint8_t key[KEY_SIZE]) {
    uint8_t buf[MAX_PACKET];
    KeyPair kp = GenerateKeyPair();
    if (!kp.pkey) return false;
    Header kh{PacketType::KEY_EXCHANGE, 0, PUB_SIZE};
    auto kpkt = MakePacket(kh, kp.pub, PUB_SIZE);

    // UDP может потерять пакет рукопожатия — повторяем несколько раз.
    for (int attempt = 1; attempt <= 3; ++attempt) {
        send(s, kpkt.data(), kpkt.size(), 0);
        int n = recv(s, buf, sizeof(buf), 0);
        if (n < (int)sizeof(Header)) { printf("handshake attempt %d: timeout\n", attempt); continue; }
        Header h; memcpy(&h, buf, sizeof(h));
        const uint8_t* body = buf + sizeof(Header);
        int len = n - (int)sizeof(Header);
        if (h.type != PacketType::KEY_EXCHANGE || h.payloadSize != len) continue;

        // По длине ответа понимаем, подписал ли сервер рукопожатие: 32 или 32 + 64 байта.
        bool signedReply = len == PUB_SIZE + SIG_SIZE;
        if (len != PUB_SIZE && !signedReply) continue;
        if (o.pinned) {
            // Неподписанный ответ при заданном pin тоже отказ: иначе атакующий просто
            // убрал бы подпись из пакета и обошёл проверку.
            if (!signedReply || !VerifyHandshake(o.pin, kp.pub, body, body + PUB_SIZE)) {
                puts("handshake REJECTED: подпись сервера не совпадает с --pin (возможен MITM)");
                FreeKeyPair(kp);
                return false;
            }
            puts("server signature OK (pinned Ed25519 key)");
        } else {
            puts("WARNING: сервер не аутентифицирован (нет --pin), MITM на обмене ключами возможен");
        }
        bool ok = DeriveSessionKey(kp.pkey, body, key);
        FreeKeyPair(kp);
        return ok;
    }
    FreeKeyPair(kp);
    return false;
}

// Рукопожатие, затем LOGIN и серия MOVEMENT/SHOOT в зашифрованном виде.
// Код возврата 0, только если на каждую команду пришёл и расшифровался ACK:
// по нему скрипт run_scenarios.sh понимает, прошёл ли сценарий.
static int RunEncrypted(int s, const Options& o) {
    uint8_t key[KEY_SIZE];
    if (!Handshake(s, o, key)) { puts("handshake failed"); return 1; }
    puts("handshake OK");

    uint64_t counter = 0;        // счётчик для nonce: 1, 2, 3... (НЕ rand(), см. EncryptAES)
    uint16_t seq = 0;            // номер пакета в открытом заголовке, как в прошлых практиках
    ReplayWindow serverWindow;   // клиент так же отсекает повторы ответов сервера
    uint8_t buf[MAX_PACKET];
    int okReplies = 0, sent = 0;

    // Шифрует одну команду (inner = [внутренний тип][тело]), отправляет её и ждёт ACK.
    // Ответ проверяется так же строго, как сервер проверяет запросы: тип, размер,
    // счётчик сервера со старшим битом, окно повторов и тег.
    auto sendEncrypted = [&](const std::vector<uint8_t>& inner, const char* what) {
        // Заголовок (вместе с payloadSize) заполняется до шифрования: он целиком входит в AAD.
        Header h{PacketType::ENCRYPTED, ++seq, (uint16_t)(NONCE_SIZE + inner.size() + TAG_SIZE)};
        auto ct = EncryptAES(key, ++counter, inner.data(), (int)inner.size(), (const uint8_t*)&h, sizeof(h));
        auto pkt = MakePacket(h, ct.data(), (int)ct.size());
        send(s, pkt.data(), pkt.size(), 0);
        ++sent;

        int r = recv(s, buf, sizeof(buf), 0);
        if (r <= (int)sizeof(Header)) { printf("%s seq=%u -> timeout\n", what, seq); return; }
        Header rh; memcpy(&rh, buf, sizeof(rh));
        const uint8_t* body = buf + sizeof(Header);
        int len = r - (int)sizeof(Header);
        uint64_t rc = CounterFromPacket(body, len);
        std::vector<uint8_t> plain;
        // Ответ без старшего бита в счётчике создан не сервером: это наш же пакет,
        // который кто-то вернул нам обратно (отражение).
        if (rh.type != PacketType::ENCRYPTED || rh.payloadSize != len || !(rc & SERVER_COUNTER_BIT) ||
            !serverWindow.Check(rc)) {
            printf("%s seq=%u -> отброшен ответ (тип/размер/счётчик)\n", what, seq);
        } else if (!DecryptAES(key, body, len, buf, sizeof(Header), plain)) {
            printf("%s seq=%u -> AUTH_FAIL\n", what, seq);
        } else {
            serverWindow.Accept(rc);   // как и на сервере: только после проверки тега
            // внутри ответа [ACK][номер подтверждённого пакета 2 байта]
            uint16_t acked = 0;
            if (plain.size() == INNER_TYPE_SIZE + sizeof(uint16_t)) memcpy(&acked, plain.data() + 1, 2);
            printf("%s seq=%u -> ACK %u (decrypted)\n", what, seq, acked);
            ++okReplies;
        }
    };

    // Тот же сценарий, что в RunPlain: снимки «до» и «после» сравниваются один к одному.
    LoginPayload l = MakeLogin(o);
    sendEncrypted(MakeInner(PacketType::LOGIN, l), "LOGIN");
    OPENSSL_cleanse(&l, sizeof(l));   // пароль больше не нужен, затираем его в памяти
    for (int i = 0; i < o.count; ++i) {
        MovementPayload m{1.0f * i, 0.0f, 2.0f * i};
        sendEncrypted(MakeInner(PacketType::MOVEMENT, m), "MOVEMENT");
        if (i % 2 == 1) {
            ShootPayload sh{1, 0.6f, 0.8f};
            sendEncrypted(MakeInner(PacketType::SHOOT, sh), "SHOOT");
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
    OPENSSL_cleanse(key, sizeof(key));   // ключ сессии затираем при выходе
    printf("done: %d/%d replies decrypted\n", okReplies, sent);
    return okReplies == sent ? 0 : 3;
}

// Точка входа: разбор флагов, подключение и запуск выбранного режима.
int main(int argc, char** argv) {
    Options o;
    if (!ParseArgs(argc, argv, o)) return 2;
    int s = Connect(o);
    if (s < 0) return 1;
    int rc = o.plain ? RunPlain(s, o) : RunEncrypted(s, o);
    close(s);
    return rc;
}
