// Самопроверка: g++ -std=c++17 crypto_test.cpp crypto.cpp -lcrypto -o crypto_test
#include "crypto.h"
#include <cstdio>
#include <cstring>
using namespace crypto;

int main() {
    KeyPair a = GenerateKeyPair(), b = GenerateKeyPair();
    uint8_t ka[KEY_SIZE], kb[KEY_SIZE];
    if (!DeriveSessionKey(a.pkey, b.pub, ka) || !DeriveSessionKey(b.pkey, a.pub, kb)) { puts("FAIL derive"); return 1; }
    printf("[%s] ключи совпадают\n", memcmp(ka, kb, KEY_SIZE) == 0 ? "OK" : "FAIL");

    const char* msg = "MOVE 12.5 0.0 7.3";
    uint8_t hdr[4] = {11, 0, 1, 0};
    auto pkt = EncryptAES(ka, 1, (const uint8_t*)msg, (int)strlen(msg), hdr, 4);
    std::vector<uint8_t> out;
    bool ok = DecryptAES(kb, pkt.data(), (int)pkt.size(), hdr, 4, out);
    printf("[%s] расшифровка\n", ok && out.size() == strlen(msg) && !memcmp(out.data(), msg, out.size()) ? "OK" : "FAIL");

    pkt[14] ^= 1;
    printf("[%s] подмена шифртекста отклонена\n", !DecryptAES(kb, pkt.data(), (int)pkt.size(), hdr, 4, out) ? "OK" : "FAIL");
    pkt[14] ^= 1; hdr[1] ^= 1;
    printf("[%s] подмена заголовка (AAD) отклонена\n", !DecryptAES(kb, pkt.data(), (int)pkt.size(), hdr, 4, out) ? "OK" : "FAIL");
    FreeKeyPair(a); FreeKeyPair(b);
}
