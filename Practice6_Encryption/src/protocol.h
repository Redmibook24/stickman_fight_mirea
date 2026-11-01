#pragma once
#include <cstdint>
#include <cstring>
#include <vector>

#pragma pack(push, 1)
enum class PacketType : uint8_t { MOVEMENT = 1, SHOOT = 2, ACK = 3, KEY_EXCHANGE = 10, ENCRYPTED = 11 };

struct Header {
    PacketType type;
    uint16_t   seq;
    uint16_t   payloadSize;
};

struct MovementPayload { float x, y, z; };
#pragma pack(pop)

constexpr int PORT = 9000;
constexpr int MAX_PACKET = 1200;

inline std::vector<uint8_t> MakePacket(const Header& h, const uint8_t* body, int len) {
    std::vector<uint8_t> p(sizeof(Header) + len);
    memcpy(p.data(), &h, sizeof(Header));
    if (len) memcpy(p.data() + sizeof(Header), body, len);
    return p;
}
