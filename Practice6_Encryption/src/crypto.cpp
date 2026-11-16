#include "crypto.h"
#include <openssl/kdf.h>
#include <openssl/pem.h>
#include <openssl/crypto.h>
#include <cstdio>
#include <cstring>

// Реализация криптомодуля ПР№6. Все примитивы берутся из OpenSSL 3 через EVP-интерфейс:
// свою криптографию не пишем, только правильно соединяем готовые алгоритмы.
// Соглашение по ошибкам: функции возвращают false или пустой результат и никогда не бросают
// исключений, потому что вызывающий код в любом случае просто отбрасывает плохой пакет.

namespace crypto {

// Создаёт эфемерную (одноразовую, на одну сессию) пару ключей X25519.
// Открытая часть сразу копируется в kp.pub: именно эти 32 байта уходят в пакете KEY_EXCHANGE.
// Закрытый ключ остаётся внутри EVP_PKEY и нужен только для DeriveSessionKey.
// При ошибке OpenSSL возвращается пара с pkey == nullptr, вызывающий код это проверяет.
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

// Освобождает ключ OpenSSL. Безопасно вызывать повторно: после первого вызова pkey = nullptr.
void FreeKeyPair(KeyPair& kp) {
    if (kp.pkey) { EVP_PKEY_free(kp.pkey); kp.pkey = nullptr; }
}

// Вычисляет общий ключ сессии из своего закрытого ключа и открытого ключа собеседника.
// Шаг 1, X25519 (Диффи-Хеллман): клиент считает DH(свой закрытый, открытый сервера),
//   сервер считает DH(свой закрытый, открытый клиента), и у обоих получается один и тот же
//   32-байтный секрет, хотя сам секрет по сети не передавался.
// Шаг 2, HKDF-SHA256: секрет X25519 это координата точки кривой, а не равномерно случайные
//   байты, поэтому напрямую как ключ AES его не берут. HKDF «выравнивает» его в ключ нужной
//   длины. Метка info = "game-udp-v1" привязывает ключ к нашему протоколу и его версии:
//   при смене формата меняем метку, и старые ключи автоматически перестают подходить.
bool DeriveSessionKey(EVP_PKEY* mine, const uint8_t peerPub[PUB_SIZE], uint8_t outKey[KEY_SIZE]) {
    // Открытый ключ собеседника приходит из сети сырыми 32 байтами, превращаем его в объект OpenSSL.
    EVP_PKEY* peer = EVP_PKEY_new_raw_public_key(EVP_PKEY_X25519, nullptr, peerPub, PUB_SIZE);
    if (!peer) return false;
    // Шаг 1: общий секрет X25519.
    uint8_t secret[32]; size_t sl = sizeof(secret);
    EVP_PKEY_CTX* c = EVP_PKEY_CTX_new(mine, nullptr);
    bool ok = c && EVP_PKEY_derive_init(c) > 0 &&
              EVP_PKEY_derive_set_peer(c, peer) > 0 &&
              EVP_PKEY_derive(c, secret, &sl) > 0;
    EVP_PKEY_CTX_free(c);
    EVP_PKEY_free(peer);
    if (!ok) return false;

    // Шаг 2: HKDF-SHA256 без соли, info = "game-udp-v1". Тот же вывод повторяет MITM-прокси
    // tools/udp_mitm.py (DhMitm.derive), иначе он не смог бы расшифровать трафик.
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

// Шифрует один пакет AES-256-GCM. Результат: [nonce 12 байт][шифртекст N байт][тег 16 байт].
//
// Nonce = 4 нулевых байта + 8 байт счётчика отправителя. Главное правило GCM: пара
// «ключ + nonce» не должна повториться никогда, поэтому берём растущий счётчик, а не rand().
// Счётчик заодно служит номером пакета для окна повторов (ReplayWindow).
//
// AAD (aad, aadLen) это открытый заголовок пакета. Он не шифруется, чтобы сервер мог прочитать
// тип и номер до расшифровки, но входит в вычисление тега: подмена заголовка будет замечена.
//
// GCM работает как потоковый шифр, поэтому шифртекст ровно той же длины, что открытый текст,
// без паддинга. При любой ошибке OpenSSL возвращается пустой вектор.
std::vector<uint8_t> EncryptAES(const uint8_t key[KEY_SIZE], uint64_t counter,
                                const uint8_t* plain, int plainLen,
                                const uint8_t* aad, int aadLen) {
    uint8_t nonce[NONCE_SIZE] = {0};
    memcpy(nonce + 4, &counter, 8);
    std::vector<uint8_t> out(NONCE_SIZE + plainLen + TAG_SIZE);
    memcpy(out.data(), nonce, NONCE_SIZE);   // nonce идёт открыто: получателю он нужен для расшифровки

    EVP_CIPHER_CTX* c = EVP_CIPHER_CTX_new();
    int l = 0, fin = 0;
    bool ok = c &&
        EVP_EncryptInit_ex(c, EVP_aes_256_gcm(), nullptr, key, nonce) == 1 &&
        // вызов с выходом nullptr подаёт данные только в тег (это AAD), а не в шифртекст
        (aadLen == 0 || EVP_EncryptUpdate(c, nullptr, &l, aad, aadLen) == 1) &&
        EVP_EncryptUpdate(c, out.data() + NONCE_SIZE, &l, plain, plainLen) == 1 &&
        EVP_EncryptFinal_ex(c, out.data() + NONCE_SIZE + l, &fin) == 1 &&
        // тег 16 байт (подпись целостности) дописывается в конец пакета
        EVP_CIPHER_CTX_ctrl(c, EVP_CTRL_GCM_GET_TAG, TAG_SIZE,
                            out.data() + NONCE_SIZE + plainLen) == 1;
    EVP_CIPHER_CTX_free(c);
    if (!ok) out.clear();
    return out;
}

// Расшифровывает пакет формата [nonce 12][шифртекст][тег 16] и проверяет его подлинность.
// Возвращает false, если тег не сошёлся: изменён хоть один бит шифртекста, nonce, тега
// или заголовка (AAD), либо пакет зашифрован другим ключом. Такой пакет надо молча отбросить,
// сервер пишет в лог AUTH_FAIL. При ошибке outPlain очищается, чтобы вызывающий код
// не мог случайно использовать непроверенные данные.
bool DecryptAES(const uint8_t key[KEY_SIZE], const uint8_t* packet, int packetLen,
                const uint8_t* aad, int aadLen, std::vector<uint8_t>& outPlain) {
    if (packetLen < NONCE_SIZE + TAG_SIZE) return false;   // не хватает даже на nonce и тег
    int cl = packetLen - NONCE_SIZE - TAG_SIZE;             // длина шифртекста
    const uint8_t* nonce = packet;
    const uint8_t* ct = packet + NONCE_SIZE;
    uint8_t tag[TAG_SIZE];
    memcpy(tag, packet + NONCE_SIZE + cl, TAG_SIZE);
    // буфер не бывает нулевой длины: data() пустого вектора может быть nullptr, а OpenSSL это не любит
    outPlain.assign(cl > 0 ? cl : 1, 0);

    EVP_CIPHER_CTX* c = EVP_CIPHER_CTX_new();
    int l = 0, fin = 0;
    bool ok = c &&
        EVP_DecryptInit_ex(c, EVP_aes_256_gcm(), nullptr, key, nonce) == 1 &&
        (aadLen == 0 || EVP_DecryptUpdate(c, nullptr, &l, aad, aadLen) == 1) &&
        EVP_DecryptUpdate(c, outPlain.data(), &l, ct, cl) == 1 &&
        // сообщаем OpenSSL ожидаемый тег из пакета...
        EVP_CIPHER_CTX_ctrl(c, EVP_CTRL_GCM_SET_TAG, TAG_SIZE, tag) == 1 &&
        // ...а Final сравнивает его с вычисленным: именно здесь ловится любая подмена
        EVP_DecryptFinal_ex(c, outPlain.data() + l, &fin) > 0;
    EVP_CIPHER_CTX_free(c);
    if (!ok) { outPlain.clear(); return false; }
    outPlain.resize(cl);
    return true;
}

// Достаёт счётчик отправителя из nonce, не расшифровывая пакет. Нужно серверу, чтобы
// до дорогой расшифровки отсечь повторы (ReplayWindow::Check) и отражения (старший бит).
uint64_t CounterFromPacket(const uint8_t* packet, int packetLen) {
    if (packetLen < NONCE_SIZE + TAG_SIZE) return 0;
    uint64_t counter = 0;
    memcpy(&counter, packet + 4, 8); // тот же порядок байт, что и в EncryptAES
    return counter;
}

// Окно повторов хранит максимальный принятый счётчик m_Max и 64-битную маску m_Bitmap:
// бит i установлен, если счётчик (m_Max - i) уже был принят. Так за 16 байт памяти
// запоминаются последние 64 пакета, а UDP-пакеты, пришедшие не по порядку, всё равно принимаются.
//
// Пример: m_Max = 10, приняты 10, 9 и 7. Маска = ...0001011 (биты 0, 1 и 3).
//   Check(8)  -> возраст 2, бит 2 пуст      -> можно принять (опоздавший пакет)
//   Check(9)  -> возраст 1, бит 1 занят     -> повтор, отбросить
//   Check(12) -> больше m_Max               -> новый пакет, принять

// Можно ли принять пакет с таким счётчиком. Ничего не меняет: вызывается ДО проверки тега.
bool ReplayWindow::Check(uint64_t counter) const {
    if (counter == 0) return false;          // счётчики начинаются с 1
    if (counter > m_Max) return true;        // новый пакет
    uint64_t age = m_Max - counter;
    if (age >= 64) return false;             // слишком старый — уже вне окна
    return (m_Bitmap & (1ull << age)) == 0;  // внутри окна: не был ли уже принят
}

// Отмечает счётчик принятым. Вызывается только ПОСЛЕ успешной проверки тега: иначе
// атакующий прислал бы мусор с огромным счётчиком, окно сдвинулось бы вперёд,
// и все настоящие пакеты клиента стали бы «слишком старыми».
void ReplayWindow::Accept(uint64_t counter) {
    if (counter > m_Max) {
        // новый максимум: сдвигаем маску на разницу и отмечаем сам пакет младшим битом;
        // при скачке на 64 и больше всё старое выпадает из окна целиком
        uint64_t shift = counter - m_Max;
        m_Bitmap = shift >= 64 ? 1 : (m_Bitmap << shift) | 1;
        m_Max = counter;
    } else {
        // опоздавший пакет внутри окна: просто ставим его бит
        m_Bitmap |= 1ull << (m_Max - counter);
    }
}

// Долговременный ключ подписи сервера (Ed25519) для защиты от MITM.
// Если файл есть, ключ читается из него; если нет, создаётся новый и сохраняется, чтобы при
// перезапуске сервера ключ не менялся: иначе все клиенты с закреплённым --pin перестали бы
// подключаться. Открытая часть (32 байта) отдаётся в pubOut, сервер печатает её как pin.
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

// Транскрипт рукопожатия: то, что подписывает сервер. Метка + ключ клиента + ключ сервера.
// В подпись входят ОБА ключа: атакующий не может ни заменить ключ сервера своим,
// ни переиспользовать чужую подпись для другого клиента. Метка не даёт выдать подпись
// из какого-то другого места за подпись рукопожатия.
static std::vector<uint8_t> HandshakeTranscript(const uint8_t clientPub[PUB_SIZE], const uint8_t serverPub[PUB_SIZE]) {
    static const char label[] = "game-udp-v1/handshake";
    std::vector<uint8_t> t(label, label + sizeof(label) - 1);
    t.insert(t.end(), clientPub, clientPub + PUB_SIZE);
    t.insert(t.end(), serverPub, serverPub + PUB_SIZE);
    return t;
}

// Сервер подписывает транскрипт своим закрытым ключом Ed25519 (подпись 64 байта).
// Ed25519 хэширует сообщение сам, поэтому хэш-функция в DigestSignInit не задаётся (nullptr).
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

// Клиент проверяет подпись закреплённым открытым ключом сервера (signerPub = --pin).
// Если при MITM атакующий подменил ключ сервера своим, транскрипт у клиента будет другим,
// и подпись настоящего сервера к нему не подойдёт: функция вернёт false.
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
