#include "crypto.h"
#include <openssl/kdf.h>
#include <openssl/pem.h>
#include <openssl/crypto.h>
#include <cstdio>
#include <cstring>

namespace crypto {

KeyPair GenerateKeyPair() {
    KeyPair kp;
    EVP_PKEY_CTX* c = EVP_PKEY_CTX_new_id(EVP_PKEY_X25519, nullptr);
    if (!c) return kp;
    if (EVP_PKEY_keygen_init(c) > 0 && EVP_PKEY_keygen(c, &kp.pkey) > 0) {
        size_t len = PUB_SIZE;
        EVP_PKEY_get_raw_public_key(kp.pkey, kp.pub, &len);
    }
    EVP_PKEY_CTX_free(c);
    return kp;
}

void FreeKeyPair(KeyPair& kp) {
    if (kp.pkey) { EVP_PKEY_free(kp.pkey); kp.pkey = nullptr; }
}

bool DeriveSessionKey(EVP_PKEY* mine, const uint8_t peerPub[PUB_SIZE], uint8_t outKey[KEY_SIZE]) {
    EVP_PKEY* peer = EVP_PKEY_new_raw_public_key(EVP_PKEY_X25519, nullptr, peerPub, PUB_SIZE);
    if (!peer) return false;
    uint8_t secret[32]; size_t sl = sizeof(secret);
    EVP_PKEY_CTX* c = EVP_PKEY_CTX_new(mine, nullptr);
    bool ok = c && EVP_PKEY_derive_init(c) > 0 &&
              EVP_PKEY_derive_set_peer(c, peer) > 0 &&
              EVP_PKEY_derive(c, secret, &sl) > 0;
    EVP_PKEY_CTX_free(c);
    EVP_PKEY_free(peer);
    if (!ok) return false;

    EVP_PKEY_CTX* k = EVP_PKEY_CTX_new_id(EVP_PKEY_HKDF, nullptr);
    if (!k) { OPENSSL_cleanse(secret, sizeof(secret)); return false; }
    size_t kl = KEY_SIZE;
    ok = EVP_PKEY_derive_init(k) > 0 &&
         EVP_PKEY_CTX_set_hkdf_md(k, EVP_sha256()) > 0 &&
         EVP_PKEY_CTX_set1_hkdf_key(k, secret, (int)sl) > 0 &&
         EVP_PKEY_CTX_add1_hkdf_info(k, (const unsigned char*)"game-udp-v1", 11) > 0 &&
         EVP_PKEY_derive(k, outKey, &kl) > 0;
    EVP_PKEY_CTX_free(k);
    OPENSSL_cleanse(secret, sizeof(secret)); // общий секрет больше не нужен — не оставляем в памяти
    return ok;
}

std::vector<uint8_t> EncryptAES(const uint8_t key[KEY_SIZE], uint64_t counter,
                                const uint8_t* plain, int plainLen,
                                const uint8_t* aad, int aadLen) {
    uint8_t nonce[NONCE_SIZE] = {0};
    memcpy(nonce + 4, &counter, 8);
    std::vector<uint8_t> out(NONCE_SIZE + plainLen + TAG_SIZE);
    memcpy(out.data(), nonce, NONCE_SIZE);

    EVP_CIPHER_CTX* c = EVP_CIPHER_CTX_new();
    int l = 0, fin = 0;
    bool ok = c &&
        EVP_EncryptInit_ex(c, EVP_aes_256_gcm(), nullptr, key, nonce) == 1 &&
        (aadLen == 0 || EVP_EncryptUpdate(c, nullptr, &l, aad, aadLen) == 1) &&
        EVP_EncryptUpdate(c, out.data() + NONCE_SIZE, &l, plain, plainLen) == 1 &&
        EVP_EncryptFinal_ex(c, out.data() + NONCE_SIZE + l, &fin) == 1 &&
        EVP_CIPHER_CTX_ctrl(c, EVP_CTRL_GCM_GET_TAG, TAG_SIZE,
                            out.data() + NONCE_SIZE + plainLen) == 1;
    EVP_CIPHER_CTX_free(c);
    if (!ok) out.clear();
    return out;
}

bool DecryptAES(const uint8_t key[KEY_SIZE], const uint8_t* packet, int packetLen,
                const uint8_t* aad, int aadLen, std::vector<uint8_t>& outPlain) {
    if (packetLen < NONCE_SIZE + TAG_SIZE) return false;
    int cl = packetLen - NONCE_SIZE - TAG_SIZE;
    const uint8_t* nonce = packet;
    const uint8_t* ct = packet + NONCE_SIZE;
    uint8_t tag[TAG_SIZE];
    memcpy(tag, packet + NONCE_SIZE + cl, TAG_SIZE);
    outPlain.assign(cl > 0 ? cl : 1, 0);

    EVP_CIPHER_CTX* c = EVP_CIPHER_CTX_new();
    int l = 0, fin = 0;
    bool ok = c &&
        EVP_DecryptInit_ex(c, EVP_aes_256_gcm(), nullptr, key, nonce) == 1 &&
        (aadLen == 0 || EVP_DecryptUpdate(c, nullptr, &l, aad, aadLen) == 1) &&
        EVP_DecryptUpdate(c, outPlain.data(), &l, ct, cl) == 1 &&
        EVP_CIPHER_CTX_ctrl(c, EVP_CTRL_GCM_SET_TAG, TAG_SIZE, tag) == 1 &&
        EVP_DecryptFinal_ex(c, outPlain.data() + l, &fin) > 0;
    EVP_CIPHER_CTX_free(c);
    if (!ok) { outPlain.clear(); return false; }
    outPlain.resize(cl);
    return true;
}

uint64_t CounterFromPacket(const uint8_t* packet, int packetLen) {
    if (packetLen < NONCE_SIZE + TAG_SIZE) return 0;
    uint64_t counter = 0;
    memcpy(&counter, packet + 4, 8); // тот же порядок байт, что и в EncryptAES
    return counter;
}

bool ReplayWindow::Check(uint64_t counter) const {
    if (counter == 0) return false;          // счётчики начинаются с 1
    if (counter > m_Max) return true;        // новый пакет
    uint64_t age = m_Max - counter;
    if (age >= 64) return false;             // слишком старый — уже вне окна
    return (m_Bitmap & (1ull << age)) == 0;  // внутри окна: не был ли уже принят
}

void ReplayWindow::Accept(uint64_t counter) {
    if (counter > m_Max) {
        uint64_t shift = counter - m_Max;
        m_Bitmap = shift >= 64 ? 1 : (m_Bitmap << shift) | 1;
        m_Max = counter;
    } else {
        m_Bitmap |= 1ull << (m_Max - counter);
    }
}

EVP_PKEY* LoadOrCreateSigningKey(const char* path, uint8_t pubOut[SIG_PUB_SIZE]) {
    EVP_PKEY* key = nullptr;
    if (FILE* f = fopen(path, "rb")) {
        key = PEM_read_PrivateKey(f, nullptr, nullptr, nullptr);
        fclose(f);
    }
    if (!key) {
        EVP_PKEY_CTX* c = EVP_PKEY_CTX_new_id(EVP_PKEY_ED25519, nullptr);
        if (c && EVP_PKEY_keygen_init(c) > 0) EVP_PKEY_keygen(c, &key);
        EVP_PKEY_CTX_free(c);
        if (!key) return nullptr;
        if (FILE* f = fopen(path, "wb")) {
            PEM_write_PrivateKey(f, key, nullptr, nullptr, 0, nullptr, nullptr);
            fclose(f);
        }
    }
    size_t len = SIG_PUB_SIZE;
    if (EVP_PKEY_get_raw_public_key(key, pubOut, &len) <= 0 || len != SIG_PUB_SIZE) {
        EVP_PKEY_free(key);
        return nullptr;
    }
    return key;
}

static std::vector<uint8_t> HandshakeTranscript(const uint8_t clientPub[PUB_SIZE], const uint8_t serverPub[PUB_SIZE]) {
    static const char label[] = "game-udp-v1/handshake";
    std::vector<uint8_t> t(label, label + sizeof(label) - 1);
    t.insert(t.end(), clientPub, clientPub + PUB_SIZE);
    t.insert(t.end(), serverPub, serverPub + PUB_SIZE);
    return t;
}

bool SignHandshake(EVP_PKEY* key, const uint8_t clientPub[PUB_SIZE], const uint8_t serverPub[PUB_SIZE],
                   uint8_t sig[SIG_SIZE]) {
    auto msg = HandshakeTranscript(clientPub, serverPub);
    EVP_MD_CTX* c = EVP_MD_CTX_new();
    size_t sl = SIG_SIZE;
    bool ok = c && EVP_DigestSignInit(c, nullptr, nullptr, nullptr, key) > 0 &&
              EVP_DigestSign(c, sig, &sl, msg.data(), msg.size()) > 0 && sl == SIG_SIZE;
    EVP_MD_CTX_free(c);
    return ok;
}

bool VerifyHandshake(const uint8_t signerPub[SIG_PUB_SIZE], const uint8_t clientPub[PUB_SIZE],
                     const uint8_t serverPub[PUB_SIZE], const uint8_t sig[SIG_SIZE]) {
    EVP_PKEY* pub = EVP_PKEY_new_raw_public_key(EVP_PKEY_ED25519, nullptr, signerPub, SIG_PUB_SIZE);
    if (!pub) return false;
    auto msg = HandshakeTranscript(clientPub, serverPub);
    EVP_MD_CTX* c = EVP_MD_CTX_new();
    bool ok = c && EVP_DigestVerifyInit(c, nullptr, nullptr, nullptr, pub) > 0 &&
              EVP_DigestVerify(c, sig, SIG_SIZE, msg.data(), msg.size()) == 1;
    EVP_MD_CTX_free(c);
    EVP_PKEY_free(pub);
    return ok;
}

} // namespace crypto
