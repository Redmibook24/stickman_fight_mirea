#pragma once
// ПР№6. Криптомодуль: X25519 + HKDF-SHA256 + AES-256-GCM, защита от повторов,
// аутентификация сервера подписью Ed25519 (защита от MITM на голом DH).
#include <cstdint>
#include <vector>
#include <openssl/evp.h>

namespace crypto {

constexpr int KEY_SIZE   = 32;  // AES-256
constexpr int PUB_SIZE   = 32;  // открытый ключ X25519
constexpr int NONCE_SIZE = 12;  // GCM
constexpr int TAG_SIZE   = 16;

// Ключ сессии общий для обоих направлений, поэтому счётчики сторон не должны пересекаться:
// у пакетов сервера установлен старший бит. Заодно это отсекает «отражённые» пакеты.
constexpr uint64_t SERVER_COUNTER_BIT = 1ull << 63;

struct KeyPair {
    EVP_PKEY* pkey = nullptr;
    uint8_t   pub[PUB_SIZE] = {0};
};

// Эфемерная пара ключей X25519 (отправляется в пакете KEY_EXCHANGE).
KeyPair GenerateKeyPair();
void    FreeKeyPair(KeyPair& kp);

// Общий секрет (X25519) -> сессионный ключ 32 байта (HKDF-SHA256).
bool DeriveSessionKey(EVP_PKEY* mine, const uint8_t peerPub[PUB_SIZE], uint8_t outKey[KEY_SIZE]);

// Формат результата: [nonce 12][ciphertext][tag 16].
// aad - открытый заголовок пакета (аутентифицируется, но не шифруется).
// counter - монотонный счётчик пакетов отправителя (НИКОГДА не повторять с тем же ключом).
std::vector<uint8_t> EncryptAES(const uint8_t key[KEY_SIZE], uint64_t counter,
                                const uint8_t* plain, int plainLen,
                                const uint8_t* aad, int aadLen);

// Возвращает false, если тег не сошёлся (пакет подделан/повреждён) - такой пакет отбросить.
bool DecryptAES(const uint8_t key[KEY_SIZE],
                const uint8_t* packet, int packetLen,
                const uint8_t* aad, int aadLen,
                std::vector<uint8_t>& outPlain);

// Счётчик отправителя из nonce пакета [nonce 12][...]. 0 — пакет слишком короткий.
uint64_t CounterFromPacket(const uint8_t* packet, int packetLen);

// Окно защиты от повторов (как в IPsec/DTLS): каждый счётчик принимается не более одного раза,
// допускается переупорядочивание в пределах последних 64 пакетов, более старые отбрасываются.
class ReplayWindow {
public:
    bool Check(uint64_t counter) const;  // можно ли принимать (до расшифровки)
    void Accept(uint64_t counter);       // отметить принятым (после проверки тега)
private:
    uint64_t m_Max = 0;
    uint64_t m_Bitmap = 0;               // бит i = принят счётчик m_Max - i
};

// ── Аутентификация сервера: долговременный ключ Ed25519 ──
constexpr int SIG_PUB_SIZE = 32;
constexpr int SIG_SIZE     = 64;

// Загружает PEM-ключ или создаёт новый и сохраняет его. pubOut — открытый ключ для клиентов.
EVP_PKEY* LoadOrCreateSigningKey(const char* path, uint8_t pubOut[SIG_PUB_SIZE]);

// Подпись транскрипта рукопожатия: "game-udp-v1/handshake" || clientPub || serverPub.
bool SignHandshake(EVP_PKEY* key, const uint8_t clientPub[PUB_SIZE], const uint8_t serverPub[PUB_SIZE],
                   uint8_t sig[SIG_SIZE]);
bool VerifyHandshake(const uint8_t signerPub[SIG_PUB_SIZE], const uint8_t clientPub[PUB_SIZE],
                     const uint8_t serverPub[PUB_SIZE], const uint8_t sig[SIG_SIZE]);

} // namespace crypto
