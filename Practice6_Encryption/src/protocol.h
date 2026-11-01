#pragma once
#include <cstdint>
#include <cstring>
#include <vector>

#pragma pack(push, 1)
// Внешний тип пакета (открытый заголовок). KEY_EXCHANGE и ENCRYPTED добавлены в ПР№6.
// MOVEMENT/SHOOT/LOGIN в открытом виде ходят только в демонстрационном режиме --plain («до шифрования»);
// в зашифрованном режиме это внутренний тип, спрятанный в шифртексте.
enum class PacketType : uint8_t {
    MOVEMENT = 1, SHOOT = 2, ACK = 3, LOGIN = 4,
    KEY_EXCHANGE = 10,  // открытый ключ X25519, без шифрования
    ENCRYPTED = 11      // nonce + шифртекст + tag
};

struct Header {
    PacketType type;
    uint16_t   seq;
    uint16_t   payloadSize;   // размер всего, что идёт после заголовка
};

struct MovementPayload { float x, y, z; };
struct ShootPayload    { uint8_t weapon; float dirX, dirZ; };
struct LoginPayload    { char user[16]; char password[16]; };
#pragma pack(pop)

// Открытый текст внутри ENCRYPTED: [внутренний тип 1 байт][тело]. Сервер не угадывает тип
// по размеру, а наблюдатель не видит, какая это команда.
constexpr int INNER_TYPE_SIZE = 1;

constexpr int PORT = 9000;
constexpr int MAX_PACKET = 1200;

inline std::vector<uint8_t> MakePacket(const Header& h, const uint8_t* body, int len) {
    std::vector<uint8_t> p(sizeof(Header) + len);
    memcpy(p.data(), &h, sizeof(Header));
    if (len) memcpy(p.data() + sizeof(Header), body, len);
    return p;
}

template <typename T>
inline std::vector<uint8_t> MakeInner(PacketType inner, const T& body) {
    std::vector<uint8_t> p(INNER_TYPE_SIZE + sizeof(T));
    p[0] = static_cast<uint8_t>(inner);
    memcpy(p.data() + INNER_TYPE_SIZE, &body, sizeof(T));
    return p;
}

// ── Cloudflare Spectrum: Simple Proxy Protocol (UDP) ──
// Spectrum добавляет к каждой датаграмме 38-байтный заголовок с реальным адресом клиента,
// а origin должен добавлять такой же заголовок к ответам. Поля — в сетевом порядке байт:
// magic 0x56EC (2) | client addr (16, IPv6 или IPv4-mapped) | proxy addr (16) | client port (2) | proxy port (2)
constexpr uint16_t SPP_MAGIC = 0x56EC;
constexpr int SPP_SIZE = 38;
