#include <cstdio>
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

struct Session { uint8_t key[KEY_SIZE]; uint64_t sendCounter = 0; };

static std::string Key(const sockaddr_in& a) {
    return std::string(inet_ntoa(a.sin_addr)) + ":" + std::to_string(ntohs(a.sin_port));
}

int main() {
    int s = socket(AF_INET, SOCK_DGRAM, 0);
    sockaddr_in addr{}; addr.sin_family = AF_INET; addr.sin_addr.s_addr = INADDR_ANY; addr.sin_port = htons(PORT);
    if (bind(s, (sockaddr*)&addr, sizeof(addr)) < 0) { perror("bind"); return 1; }
    std::map<std::string, Session> sessions;
    printf("Server on UDP %d\n", PORT); fflush(stdout);

    uint8_t buf[MAX_PACKET];
    while (true) {
        sockaddr_in from{}; socklen_t fl = sizeof(from);
        int n = recvfrom(s, buf, sizeof(buf), 0, (sockaddr*)&from, &fl);
        if (n < (int)sizeof(Header)) continue;
        Header h; memcpy(&h, buf, sizeof(h));

        if (h.type == PacketType::KEY_EXCHANGE && n == (int)sizeof(Header) + PUB_SIZE) {
            KeyPair kp = GenerateKeyPair();
            Session ses;
            if (!DeriveSessionKey(kp.pkey, buf + sizeof(Header), ses.key)) { FreeKeyPair(kp); continue; }
            sessions[Key(from)] = ses;
            Header rh{PacketType::KEY_EXCHANGE, 0, PUB_SIZE};
            auto rp = MakePacket(rh, kp.pub, PUB_SIZE);
            sendto(s, rp.data(), rp.size(), 0, (sockaddr*)&from, fl);
            FreeKeyPair(kp);
            printf("[%s] session established\n", Key(from).c_str());
        } else if (h.type == PacketType::ENCRYPTED) {
            auto it = sessions.find(Key(from));
            if (it == sessions.end()) continue;
            std::vector<uint8_t> plain;
            if (!DecryptAES(it->second.key, buf + sizeof(Header), n - sizeof(Header), buf, sizeof(Header), plain)) {
                printf("[%s] AUTH_FAIL seq=%u\n", Key(from).c_str(), h.seq);
                continue;
            }
            if (plain.size() == sizeof(MovementPayload)) {
                MovementPayload m; memcpy(&m, plain.data(), sizeof(m));
                printf("[%s] MOVEMENT seq=%u (%.1f, %.1f, %.1f)\n", Key(from).c_str(), h.seq, m.x, m.y, m.z);
                Header ah{PacketType::ENCRYPTED, h.seq, 0};
                auto ct = EncryptAES(it->second.key, ++it->second.sendCounter + (1ull << 63),
                                     plain.data(), plain.size(), (uint8_t*)&ah, sizeof(ah));
                ah.payloadSize = ct.size();
                auto rp = MakePacket(ah, ct.data(), ct.size());
                sendto(s, rp.data(), rp.size(), 0, (sockaddr*)&from, fl);
            }
        }
        fflush(stdout);
    }
}
