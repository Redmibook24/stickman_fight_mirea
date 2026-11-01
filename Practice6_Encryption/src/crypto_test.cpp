// Самопроверка: g++ -std=c++17 crypto_test.cpp crypto.cpp -lcrypto -o crypto_test
#include "crypto.h"
#include <cstdio>
#include <cstring>
#include <set>
#include <vector>
using namespace crypto;

static int g_Failed = 0;
static void Check(bool ok, const char* what) {
    printf("[%s] %s\n", ok ? "OK" : "FAIL", what);
    if (!ok) ++g_Failed;
}

int main() {
    KeyPair a = GenerateKeyPair(), b = GenerateKeyPair();
    uint8_t ka[KEY_SIZE], kb[KEY_SIZE];
    if (!DeriveSessionKey(a.pkey, b.pub, ka) || !DeriveSessionKey(b.pkey, a.pub, kb)) { puts("FAIL derive"); return 1; }
    Check(memcmp(ka, kb, KEY_SIZE) == 0, "ключи совпадают");

    const char* msg = "MOVE 12.5 0.0 7.3";
    uint8_t hdr[5] = {11, 1, 0, 0, 0};
    auto pkt = EncryptAES(ka, 1, (const uint8_t*)msg, (int)strlen(msg), hdr, 5);
    std::vector<uint8_t> out;
    bool ok = DecryptAES(kb, pkt.data(), (int)pkt.size(), hdr, 5, out);
    Check(ok && out.size() == strlen(msg) && !memcmp(out.data(), msg, out.size()), "расшифровка");
    Check(pkt.size() == NONCE_SIZE + strlen(msg) + TAG_SIZE, "размер пакета = 12 + N + 16");
    Check(memmem(pkt.data(), pkt.size(), msg, strlen(msg)) == nullptr, "открытый текст не виден в пакете");

    pkt[14] ^= 1;
    Check(!DecryptAES(kb, pkt.data(), (int)pkt.size(), hdr, 5, out), "подмена шифртекста отклонена");
    pkt[14] ^= 1;
    pkt[2] ^= 1;
    Check(!DecryptAES(kb, pkt.data(), (int)pkt.size(), hdr, 5, out), "подмена nonce отклонена");
    pkt[2] ^= 1;
    pkt[pkt.size() - 1] ^= 1;
    Check(!DecryptAES(kb, pkt.data(), (int)pkt.size(), hdr, 5, out), "подмена тега отклонена");
    pkt[pkt.size() - 1] ^= 1;
    hdr[1] ^= 1;
    Check(!DecryptAES(kb, pkt.data(), (int)pkt.size(), hdr, 5, out), "подмена заголовка (AAD) отклонена");
    hdr[1] ^= 1;
    Check(!DecryptAES(kb, pkt.data(), (int)pkt.size() - 3, hdr, 5, out), "обрезанный пакет отклонён");

    KeyPair c = GenerateKeyPair();
    uint8_t kc[KEY_SIZE];
    DeriveSessionKey(c.pkey, a.pub, kc);
    Check(!DecryptAES(kc, pkt.data(), (int)pkt.size(), hdr, 5, out), "чужой ключ сессии не расшифровывает");

    // nonce: счётчик, а не rand()
    Check(CounterFromPacket(pkt.data(), (int)pkt.size()) == 1, "счётчик читается из nonce");
    std::set<std::vector<uint8_t>> nonces;
    for (uint64_t i = 1; i <= 1000; ++i) {
        auto p = EncryptAES(ka, i, (const uint8_t*)msg, 4, hdr, 5);
        nonces.insert(std::vector<uint8_t>(p.begin(), p.begin() + NONCE_SIZE));
    }
    Check(nonces.size() == 1000, "1000 пакетов — 1000 разных nonce");
    auto p1 = EncryptAES(ka, 7, (const uint8_t*)msg, 4, hdr, 5);
    auto p2 = EncryptAES(ka, 7 | SERVER_COUNTER_BIT, (const uint8_t*)msg, 4, hdr, 5);
    Check(memcmp(p1.data(), p2.data(), NONCE_SIZE) != 0, "nonce клиента и сервера не пересекаются");

    // окно защиты от повторов
    ReplayWindow w;
    Check(!w.Check(0), "счётчик 0 не принимается");
    Check(w.Check(1), "первый пакет принят");
    w.Accept(1);
    Check(!w.Check(1), "повтор того же пакета отклонён");
    w.Accept(5);
    Check(w.Check(3), "опоздавший пакет внутри окна принят");
    w.Accept(3);
    Check(!w.Check(3), "повтор опоздавшего пакета отклонён");
    w.Accept(200);
    Check(!w.Check(100), "пакет старше окна (64) отклонён");
    Check(w.Check(199) && w.Check(150), "непринятые пакеты в окне ещё допустимы");

    // аутентификация сервера (Ed25519)
    remove("test_sign.pem");
    uint8_t spub[SIG_PUB_SIZE], spub2[SIG_PUB_SIZE], sig[SIG_SIZE];
    EVP_PKEY* sk = LoadOrCreateSigningKey("test_sign.pem", spub);
    EVP_PKEY* sk2 = LoadOrCreateSigningKey("test_sign.pem", spub2);
    Check(sk && sk2 && memcmp(spub, spub2, SIG_PUB_SIZE) == 0, "ключ подписи сохраняется и загружается");
    Check(SignHandshake(sk, a.pub, b.pub, sig) && VerifyHandshake(spub, a.pub, b.pub, sig), "подпись рукопожатия проверяется");
    Check(!VerifyHandshake(spub, a.pub, c.pub, sig), "подмена ключа сервера (MITM) обнаружена");
    Check(!VerifyHandshake(spub, c.pub, b.pub, sig), "подмена ключа клиента (MITM) обнаружена");
    EVP_PKEY_free(sk);
    EVP_PKEY_free(sk2);
    remove("test_sign.pem");

    FreeKeyPair(a); FreeKeyPair(b); FreeKeyPair(c);
    printf("%s: %d проверок не прошло\n", g_Failed ? "FAIL" : "ALL OK", g_Failed);
    return g_Failed ? 1 : 0;
}
