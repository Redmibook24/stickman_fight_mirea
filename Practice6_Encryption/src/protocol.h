#pragma once
// Формат пакетов ПР№6: общий для клиента, сервера и Python-инструментов (udp_mitm.py
// повторяет этот заголовок через struct "<BHH", поэтому менять его нужно в обоих местах).
#include <cstdint>
#include <cstring>
#include <vector>

// pack(1) убирает выравнивание: структура лежит в памяти ровно так, как идёт по сети
// (заголовок 5 байт, а не 6), и её можно копировать в пакет и обратно через memcpy.
// Порядок байт полей это порядок платформы (little-endian на x86 и ARM): для учебного стенда
// достаточно, в протоколе для разных платформ поля нужно писать в сетевом порядке.
#pragma pack(push, 1)
// Внешний тип пакета (открытый заголовок). KEY_EXCHANGE и ENCRYPTED добавлены в ПР№6.
// MOVEMENT/SHOOT/LOGIN в открытом виде ходят только в демонстрационном режиме --plain («до шифрования»);
// в зашифрованном режиме это внутренний тип, спрятанный в шифртексте.
enum class PacketType : uint8_t {
    MOVEMENT = 1, SHOOT = 2, ACK = 3, LOGIN = 4,
    KEY_EXCHANGE = 10,  // открытый ключ X25519, без шифрования
    ENCRYPTED = 11      // nonce + шифртекст + tag
};

// Открытый заголовок любого пакета, 5 байт. В ENCRYPTED он не шифруется (сервер читает тип
// и номер до расшифровки), но целиком входит в AAD, поэтому подменить его незаметно нельзя.
struct Header {
    PacketType type;
    uint16_t   seq;           // номер пакета у отправителя, как в ПР№1-3 (для ACK и повторов)
    uint16_t   payloadSize;   // размер всего, что идёт после заголовка
};

// Тела команд. В зашифрованном режиме они лежат внутри шифртекста после байта внутреннего типа.
struct MovementPayload { float x, y, z; };                          // 12 байт
struct ShootPayload    { uint8_t weapon; float dirX, dirZ; };       // 9 байт
struct LoginPayload    { char user[16]; char password[16]; };       // 32 байта, строки с нулём в конце
#pragma pack(pop)

// Открытый текст внутри ENCRYPTED: [внутренний тип 1 байт][тело]. Сервер не угадывает тип
// по размеру, а наблюдатель не видит, какая это команда.
constexpr int INNER_TYPE_SIZE = 1;

constexpr int PORT = 9000;
// Предел размера датаграммы: меньше типичного MTU (1500) с запасом на заголовки IP/UDP
// и SPP, чтобы пакет не дробился на фрагменты по пути.
constexpr int MAX_PACKET = 1200;

// Склеивает заголовок и тело в готовую датаграмму.
inline std::vector<uint8_t> MakePacket(const Header& h, const uint8_t* body, int len) {
    std::vector<uint8_t> p(sizeof(Header) + len);
    memcpy(p.data(), &h, sizeof(Header));
    if (len) memcpy(p.data() + sizeof(Header), body, len);
    return p;
}

// Собирает открытый текст для шифрования: [внутренний тип 1 байт][тело команды].
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
