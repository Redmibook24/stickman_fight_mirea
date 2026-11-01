#include <cstdio>
#include <vector>
#include <thread>
#include <chrono>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include "protocol.h"
#include "crypto.h"
using namespace crypto;

int main(int argc, char** argv) {
    const char* host = argc > 1 ? argv[1] : "127.0.0.1";
    int s = socket(AF_INET, SOCK_DGRAM, 0);
    timeval tv{2, 0}; setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    sockaddr_in srv{}; srv.sin_family = AF_INET; srv.sin_port = htons(PORT); inet_pton(AF_INET, host, &srv.sin_addr);

    uint8_t key[KEY_SIZE]; uint64_t counter = 0; uint16_t seq = 0;
    uint8_t buf[MAX_PACKET];

    KeyPair kp = GenerateKeyPair();
    Header kh{PacketType::KEY_EXCHANGE, seq++, PUB_SIZE};
    auto kpkt = MakePacket(kh, kp.pub, PUB_SIZE);
    sendto(s, kpkt.data(), kpkt.size(), 0, (sockaddr*)&srv, sizeof(srv));
    int n = recv(s, buf, sizeof(buf), 0);
    if (n != (int)sizeof(Header) + PUB_SIZE || !DeriveSessionKey(kp.pkey, buf + sizeof(Header), key)) {
        puts("handshake failed"); return 1;
    }
    FreeKeyPair(kp);
    puts("handshake OK");

    for (int i = 0; i < 5; ++i) {
        MovementPayload m{1.0f * i, 0.0f, 2.0f * i};
        Header h{PacketType::ENCRYPTED, seq++, 0};
        auto ct = EncryptAES(key, ++counter, (uint8_t*)&m, sizeof(m), (uint8_t*)&h, sizeof(h));
        h.payloadSize = ct.size();
        auto pkt = MakePacket(h, ct.data(), ct.size());
        sendto(s, pkt.data(), pkt.size(), 0, (sockaddr*)&srv, sizeof(srv));
        int r = recv(s, buf, sizeof(buf), 0);
        if (r > (int)sizeof(Header)) {
            std::vector<uint8_t> plain;
            bool ok = DecryptAES(key, buf + sizeof(Header), r - sizeof(Header), buf, sizeof(Header), plain);
            printf("reply seq=%u %s\n", ((Header*)buf)->seq, ok ? "OK (decrypted)" : "AUTH_FAIL");
        } else puts("timeout");
        std::this_thread::sleep_for(std::chrono::milliseconds(300));
    }
}
