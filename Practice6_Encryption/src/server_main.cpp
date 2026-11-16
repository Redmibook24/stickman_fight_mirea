// UDP-сервер ПР№6: обмен ключами X25519, AES-256-GCM, защита от повторов и подмены.
//   udp_server [--port 9000] [--allow-plain] [--spp] [--sign-key server_ed25519.pem] [--max-sessions 1024]
//   --allow-plain  принимать открытые MOVEMENT/SHOOT/LOGIN (только для снимка трафика «до шифрования»)
//   --spp          за Cloudflare Spectrum: разбирать Simple Proxy Protocol и видеть реальный IP клиента
//   --sign-key     подписывать рукопожатие ключом Ed25519 (клиент проверяет его по --pin)
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <openssl/crypto.h>
#include <map>
#include <string>
#include <vector>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include "protocol.h"
#include "crypto.h"
using namespace crypto;

// Общая схема работы сервера (функция main):
//   1. KEY_EXCHANGE  -> создать свою пару ключей X25519, вывести ключ сессии, ответить своим
//                      открытым ключом (и подписью Ed25519, если задан --sign-key).
//   2. ENCRYPTED     -> найти сессию по адресу, отсечь отражение и повтор по счётчику из nonce,
//                      расшифровать с проверкой тега, выполнить команду, ответить зашифрованным ACK.
//   3. открытые MOVEMENT/SHOOT/LOGIN -> принять только с --allow-plain (снимок «до»), иначе DROP.
// Любой пакет, не прошедший проверку, молча отбрасывается с записью в лог: отвечать на мусор
// нельзя, иначе сервер можно использовать для разведки или для усиления DDoS.

// Состояние одного клиента. UDP не имеет соединений, поэтому всё, что в TCP хранило бы
// соединение, сервер хранит сам в таблице сессий по адресу клиента.
struct Session {
    uint8_t key[KEY_SIZE];
    uint64_t sendCounter = 0;   // счётчик ответов сервера (к нему добавляется SERVER_COUNTER_BIT)
    ReplayWindow recvWindow;    // какие счётчики клиента уже приняты
};

// Параметры командной строки сервера (см. описание флагов в начале файла).
struct Options {
    int port = PORT;
    bool allowPlain = false;
    bool spp = false;
    const char* signKey = nullptr;
    size_t maxSessions = 1024;
};

// Откуда пришла датаграмма: адрес сокета (а за Spectrum — адрес прокси) и реальный клиент.
struct Peer {
    sockaddr_in from{};
    socklen_t fromLen = sizeof(sockaddr_in);
    std::string id;             // ключ таблицы сессий: реальный ip:port клиента
    std::string via;            // адрес, с которого пришёл пакет (IP Cloudflare при --spp)
    uint8_t spp[SPP_SIZE];      // заголовок SPP, который нужно вернуть в ответе
    bool hasSpp = false;
};

// "ip:port" из адреса сокета: ключ таблицы сессий и подпись строк лога.
static std::string AddrToString(const sockaddr_in& a) {
    char ip[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &a.sin_addr, ip, sizeof(ip));
    return std::string(ip) + ":" + std::to_string(ntohs(a.sin_port));
}

// Реальный адрес клиента из заголовка Simple Proxy Protocol (см. формат в protocol.h).
// Адрес лежит в байтах 2-17 как IPv6; IPv4-клиент записан в виде ::ffff:a.b.c.d
// (IPv4-mapped), его распознаём по 12-байтному префиксу и печатаем как обычный IPv4.
static std::string SppClientToString(const uint8_t* spp) {
    const uint8_t* addr = spp + 2;
    uint16_t port = (uint16_t)(spp[34] << 8 | spp[35]);
    static const uint8_t v4mapped[12] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff};
    char ip[INET6_ADDRSTRLEN];
    if (memcmp(addr, v4mapped, 12) == 0) inet_ntop(AF_INET, addr + 12, ip, sizeof(ip));
    else inet_ntop(AF_INET6, addr, ip, sizeof(ip));
    return std::string(ip) + ":" + std::to_string(port);
}

static void Log(const Peer& p, const char* fmt, ...) __attribute__((format(printf, 2, 3)));
// Строка лога: время, адрес клиента (за Spectrum: «клиент via прокси»), сообщение в стиле printf.
// fflush после каждой строки: лог пишется в файл и должен быть полным даже при kill сервера.
static void Log(const Peer& p, const char* fmt, ...) {
    char ts[16];
    time_t now = time(nullptr);
    strftime(ts, sizeof(ts), "%H:%M:%S", localtime(&now));
    if (p.via != p.id) printf("%s [%s via %s] ", ts, p.id.c_str(), p.via.c_str());
    else printf("%s [%s] ", ts, p.id.c_str());
    va_list args;
    va_start(args, fmt);
    vprintf(fmt, args);
    va_end(args);
    printf("\n");
    fflush(stdout);
}

// Отправляет готовый пакет тому, от кого пришёл запрос. Все ответы сервера идут через эту
// функцию, поэтому работа за Spectrum (возврат SPP-заголовка) решается в одном месте.
static void SendRaw(int s, const Peer& p, const std::vector<uint8_t>& pkt) {
    if (!p.hasSpp) {
        sendto(s, pkt.data(), pkt.size(), 0, (const sockaddr*)&p.from, p.fromLen);
        return;
    }
    // Spectrum требует, чтобы ответ клиенту начинался с того же заголовка SPP.
    std::vector<uint8_t> out(p.spp, p.spp + SPP_SIZE);
    out.insert(out.end(), pkt.begin(), pkt.end());
    sendto(s, out.data(), out.size(), 0, (const sockaddr*)&p.from, p.fromLen);
}

// Шифрует внутреннее сообщение и отправляет его как ENCRYPTED.
static void SendEncrypted(int s, const Peer& p, Session& ses, uint16_t seq, const std::vector<uint8_t>& inner) {
    // Размер тела известен заранее, и заголовок с ним же идёт в AAD.
    // (В каркасе payloadSize заполнялся ПОСЛЕ шифрования, и тег никогда не сходился.)
    Header h{PacketType::ENCRYPTED, seq, (uint16_t)(NONCE_SIZE + inner.size() + TAG_SIZE)};
    // Счётчик сервера со старшим битом: nonce ответов не совпадёт с nonce клиента,
    // хотя ключ сессии у направлений общий (повтор nonce в GCM раскрыл бы ключ тега).
    auto ct = EncryptAES(ses.key, ++ses.sendCounter | SERVER_COUNTER_BIT,
                         inner.data(), (int)inner.size(), (const uint8_t*)&h, sizeof(h));
    if (ct.empty()) return;
    SendRaw(s, p, MakePacket(h, ct.data(), (int)ct.size()));
}

// Разбор флагов. Неизвестный флаг это ошибка, а не молчаливое игнорирование: опечатка
// вроде --alow-plain иначе незаметно запустила бы сервер не в том режиме.
static bool ParseArgs(int argc, char** argv, Options& o) {
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--port" && i + 1 < argc) o.port = atoi(argv[++i]);
        else if (a == "--allow-plain") o.allowPlain = true;
        else if (a == "--spp") o.spp = true;
        else if (a == "--sign-key" && i + 1 < argc) o.signKey = argv[++i];
        else if (a == "--max-sessions" && i + 1 < argc) o.maxSessions = (size_t)atoi(argv[++i]);
        else { fprintf(stderr, "unknown argument: %s\n", argv[i]); return false; }
    }
    return true;
}

// Обработка ОТКРЫТОГО пакета. Вызывается только с флагом --allow-plain.
static void HandlePlain(int s, const Peer& p, const Header& h, const uint8_t* body, int len) {
    // Демонстрационный режим «до шифрования»: всё видно в Wireshark.
    // Размер тела сверяется с типом: memcpy в структуру из чужого пакета без этой проверки
    // прочитал бы память за концом буфера.
    if (h.type == PacketType::LOGIN && len == (int)sizeof(LoginPayload)) {
        LoginPayload l; memcpy(&l, body, sizeof(l));
        Log(p, "PLAIN LOGIN seq=%u user=%.16s", h.seq, l.user);
    } else if (h.type == PacketType::MOVEMENT && len == (int)sizeof(MovementPayload)) {
        MovementPayload m; memcpy(&m, body, sizeof(m));
        Log(p, "PLAIN MOVEMENT seq=%u (%.1f, %.1f, %.1f)", h.seq, m.x, m.y, m.z);
    } else if (h.type == PacketType::SHOOT && len == (int)sizeof(ShootPayload)) {
        ShootPayload sh; memcpy(&sh, body, sizeof(sh));
        Log(p, "PLAIN SHOOT seq=%u weapon=%u dir=(%.2f, %.2f)", h.seq, sh.weapon, sh.dirX, sh.dirZ);
    } else {
        Log(p, "PLAIN malformed seq=%u", h.seq);
        return;
    }
    Header ah{PacketType::ACK, h.seq, 0};
    SendRaw(s, p, MakePacket(ah, nullptr, 0));
}

// Выполнение команды из УЖЕ расшифрованного и проверенного пакета. plain = [внутренний тип][тело].
// Сюда пакет попадает только после проверки тега, то есть содержимое точно от владельца ключа.
// Здесь место игровой логики: в учебной версии команда просто пишется в лог.
static void HandleInner(int s, const Peer& p, Session& ses, const Header& h, const std::vector<uint8_t>& plain) {
    if (plain.size() < (size_t)INNER_TYPE_SIZE) return;
    auto inner = static_cast<PacketType>(plain[0]);
    const uint8_t* body = plain.data() + INNER_TYPE_SIZE;
    size_t len = plain.size() - INNER_TYPE_SIZE;

    if (inner == PacketType::LOGIN && len == sizeof(LoginPayload)) {
        LoginPayload l; memcpy(&l, body, sizeof(l));
        // сам пароль в лог не пишем, только его длину: лог не должен стать новой утечкой
        Log(p, "LOGIN seq=%u user=%.16s (пароль получен, %zu симв.)", h.seq, l.user, strnlen(l.password, 16));
        OPENSSL_cleanse(&l, sizeof(l));   // затираем пароль в памяти сразу после использования
    } else if (inner == PacketType::MOVEMENT && len == sizeof(MovementPayload)) {
        MovementPayload m; memcpy(&m, body, sizeof(m));
        Log(p, "MOVEMENT seq=%u (%.1f, %.1f, %.1f)", h.seq, m.x, m.y, m.z);
    } else if (inner == PacketType::SHOOT && len == sizeof(ShootPayload)) {
        ShootPayload sh; memcpy(&sh, body, sizeof(sh));
        Log(p, "SHOOT seq=%u weapon=%u dir=(%.2f, %.2f)", h.seq, sh.weapon, sh.dirX, sh.dirZ);
    } else {
        Log(p, "malformed inner type=%u len=%zu", (unsigned)plain[0], len);
        return;
    }
    // Подтверждение тоже шифруется: внутри — ACK и номер подтверждённого пакета.
    std::vector<uint8_t> ack(INNER_TYPE_SIZE + sizeof(uint16_t));
    ack[0] = static_cast<uint8_t>(PacketType::ACK);
    memcpy(ack.data() + INNER_TYPE_SIZE, &h.seq, sizeof(uint16_t));
    SendEncrypted(s, p, ses, h.seq, ack);
}

// Точка входа: подготовка ключа подписи и сокета, затем бесконечный цикл приёма датаграмм.
int main(int argc, char** argv) {
    Options opt;
    if (!ParseArgs(argc, argv, opt)) return 2;

    // Ключ подписи нужен только для защиты от MITM. Его открытая часть печатается как pin:
    // её передают клиентам заранее (вместе с игрой), клиент запускается с --pin <hex>.
    EVP_PKEY* signKey = nullptr;
    uint8_t signPub[SIG_PUB_SIZE];
    if (opt.signKey) {
        signKey = LoadOrCreateSigningKey(opt.signKey, signPub);
        if (!signKey) { fprintf(stderr, "cannot load signing key %s\n", opt.signKey); return 1; }
        printf("Handshake signing key (Ed25519), pin for clients: ");
        for (uint8_t b : signPub) printf("%02x", b);
        printf("\n");
    }

    int s = socket(AF_INET, SOCK_DGRAM, 0);
    sockaddr_in addr{}; addr.sin_family = AF_INET; addr.sin_addr.s_addr = INADDR_ANY; addr.sin_port = htons(opt.port);
    if (bind(s, (sockaddr*)&addr, sizeof(addr)) < 0) { perror("bind"); return 1; }
    // Таблица сессий: "ip:port" клиента -> ключ, счётчик и окно повторов.
    std::map<std::string, Session> sessions;
    printf("Server on UDP %d%s%s\n", opt.port, opt.allowPlain ? " (plain allowed)" : "", opt.spp ? " (Spectrum SPP)" : "");
    fflush(stdout);

    // Буфер с запасом под SPP-заголовок, который Spectrum добавляет к каждой датаграмме.
    uint8_t raw[MAX_PACKET + SPP_SIZE];
    while (true) {
        Peer p;
        int n = recvfrom(s, raw, sizeof(raw), 0, (sockaddr*)&p.from, &p.fromLen);
        if (n <= 0) continue;
        // Без Spectrum отправитель и клиент совпадают; с --spp id ниже заменится реальным адресом.
        p.via = AddrToString(p.from);
        p.id = p.via;

        const uint8_t* buf = raw;
        if (opt.spp) {
            // За Spectrum каждая датаграмма начинается с SPP. Без него пакет пришёл не через прокси.
            if (n < SPP_SIZE + (int)sizeof(Header) || (uint16_t)(raw[0] << 8 | raw[1]) != SPP_MAGIC) {
                Log(p, "DROP: нет заголовка Simple Proxy Protocol");
                continue;
            }
            memcpy(p.spp, raw, SPP_SIZE);     // запоминаем: его надо вернуть в ответе
            p.hasSpp = true;
            p.id = SppClientToString(raw);    // сессии и баны ведём по реальному адресу игрока
            buf += SPP_SIZE;                  // дальше разбираем пакет уже без заголовка прокси
            n -= SPP_SIZE;
        }
        // Общие проверки до любой обработки: хватает байт на заголовок, и заявленный размер
        // тела совпадает с фактическим. Обрезанные и склеенные пакеты отсекаются здесь.
        if (n < (int)sizeof(Header)) continue;
        Header h; memcpy(&h, buf, sizeof(h));
        const uint8_t* body = buf + sizeof(Header);
        int bodyLen = n - (int)sizeof(Header);
        if (h.payloadSize != bodyLen) {
            Log(p, "DROP: payloadSize=%u, фактически %d", h.payloadSize, bodyLen);
            continue;
        }

        // ── 1. Рукопожатие: клиент прислал свой открытый ключ X25519 ──
        if (h.type == PacketType::KEY_EXCHANGE && bodyLen == PUB_SIZE) {
            // лимит сессий: флуд рукопожатиями с разных адресов не исчерпает память сервера
            if (!sessions.count(p.id) && sessions.size() >= opt.maxSessions) {
                Log(p, "DROP: достигнут лимит сессий (%zu)", opt.maxSessions);
                continue;
            }
            // Новая пара ключей на каждое рукопожатие (эфемерный DH): даже если долговременный
            // ключ подписи когда-нибудь утечёт, записанный раньше трафик не расшифровать.
            KeyPair kp = GenerateKeyPair();
            Session ses;
            if (!kp.pkey || !DeriveSessionKey(kp.pkey, body, ses.key)) { FreeKeyPair(kp); continue; }
            // Повторный KEY_EXCHANGE с того же адреса (клиент перезапустился) заменяет сессию
            // целиком: новый ключ, счётчики и окно повторов с нуля.
            bool rekey = sessions.count(p.id) > 0;
            sessions[p.id] = ses;

            // Ответ: свой открытый ключ, а при --sign-key ещё подпись Ed25519 транскрипта.
            std::vector<uint8_t> reply(kp.pub, kp.pub + PUB_SIZE);
            if (signKey) {
                uint8_t sig[SIG_SIZE];
                if (!SignHandshake(signKey, body, kp.pub, sig)) { FreeKeyPair(kp); continue; }
                reply.insert(reply.end(), sig, sig + SIG_SIZE);
            }
            Header rh{PacketType::KEY_EXCHANGE, 0, (uint16_t)reply.size()};
            SendRaw(s, p, MakePacket(rh, reply.data(), (int)reply.size()));
            FreeKeyPair(kp);   // закрытый ключ больше не нужен: ключ сессии уже выведен
            Log(p, "session %s%s", rekey ? "re-established" : "established", signKey ? " (signed)" : "");
        } else if (h.type == PacketType::ENCRYPTED) {
            // ── 2. Зашифрованная команда ──
            // Порядок проверок важен: сначала дешёвые (сессия, направление, повтор), и только потом
            // дорогая расшифровка. Так флуд старыми или отражёнными пакетами почти не нагружает CPU.
            auto it = sessions.find(p.id);
            if (it == sessions.end()) { Log(p, "DROP: нет сессии"); continue; }
            Session& ses = it->second;

            // Счётчик читается из открытого nonce, без расшифровки.
            uint64_t counter = CounterFromPacket(body, bodyLen);
            if (counter & SERVER_COUNTER_BIT) {
                // Пакет зашифрован ключом этой сессии, но со счётчиком сервера: кто-то вернул нам наш же ответ.
                Log(p, "REFLECT seq=%u counter=0x%016llx — отброшен", h.seq, (unsigned long long)counter);
                continue;
            }
            // Этот счётчик уже принимали или он старше окна в 64 пакета: перехваченный пакет
            // прислали повторно (например, чтобы выстрел засчитался дважды).
            if (!ses.recvWindow.Check(counter)) {
                Log(p, "REPLAY seq=%u counter=%llu — отброшен", h.seq, (unsigned long long)counter);
                continue;
            }
            // Расшифровка с проверкой тега. Заголовок (buf) передаётся как AAD: если в пути
            // поменяли тип, номер или размер, тег не сойдётся так же, как при подмене шифртекста.
            std::vector<uint8_t> plain;
            if (!DecryptAES(ses.key, body, bodyLen, buf, sizeof(Header), plain)) {
                Log(p, "AUTH_FAIL seq=%u", h.seq);
                continue;
            }
            ses.recvWindow.Accept(counter); // только после проверки тега, иначе подделка сдвинула бы окно
            HandleInner(s, p, ses, h, plain);
        } else if (h.type == PacketType::MOVEMENT || h.type == PacketType::SHOOT || h.type == PacketType::LOGIN) {
            // ── 3. Открытая команда (без шифрования) ──
            // Принимается только в демо-режиме --allow-plain. Без флага сервер требует шифрования:
            // иначе атакующий просто слал бы открытые пакеты в обход всей защиты.
            if (opt.allowPlain) HandlePlain(s, p, h, body, bodyLen);
            else Log(p, "DROP: открытый пакет type=%u (шифрование обязательно)", (unsigned)h.type);
        }
    }
}
