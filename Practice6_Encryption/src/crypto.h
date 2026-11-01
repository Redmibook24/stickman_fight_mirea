#pragma once
// ПР№6. Каркас криптомодуля. Студенты реализуют функции в crypto.cpp (помечены TODO).
#include <cstdint>
#include <vector>
#include <openssl/evp.h>

namespace crypto {

constexpr int KEY_SIZE   = 32;  // AES-256
constexpr int PUB_SIZE   = 32;  // открытый ключ X25519
constexpr int NONCE_SIZE = 12;  // GCM
constexpr int TAG_SIZE   = 16;

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

} // namespace crypto
