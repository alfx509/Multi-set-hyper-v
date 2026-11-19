// Licensing.cpp - client side of [MS-RDPELE] for the MultiSeat Local provider (see Licensing.h).

#include "Licensing.h"
#include <bcrypt.h>
#include <cstdio>
#include <cstring>

#pragma comment(lib, "bcrypt.lib")

void Log(const wchar_t* format, ...);                          // MultiSeatProtocol.cpp
void LogHex(const wchar_t* label, const BYTE* data, ULONG size);

namespace
{
    // [MS-RDPBCGR] 2.2.1.12.1.1 / [MS-RDPELE] 2.2.2 message types and blob types.
    constexpr BYTE LICENSE_REQUEST = 0x01;
    constexpr BYTE PLATFORM_CHALLENGE = 0x02;
    constexpr BYTE NEW_LICENSE = 0x03;
    constexpr BYTE UPGRADE_LICENSE = 0x04;
    constexpr BYTE NEW_LICENSE_REQUEST = 0x13;
    constexpr BYTE PLATFORM_CHALLENGE_RESPONSE = 0x15;
    constexpr BYTE ERROR_ALERT = 0xFF;
    constexpr BYTE PREAMBLE_FLAGS = 0x03 | 0x80;                // PREAMBLE_VERSION_3_0 | EXTENDED_ERROR_MSG_SUPPORTED
    constexpr WORD BB_RANDOM_BLOB = 0x0002;
    constexpr WORD BB_ENCRYPTED_DATA_BLOB = 0x0009;
    constexpr WORD BB_CLIENT_USER_NAME_BLOB = 0x000F;
    constexpr WORD BB_CLIENT_MACHINE_NAME_BLOB = 0x0010;
    constexpr DWORD KEY_EXCHANGE_ALG_RSA = 0x00000001;
    constexpr DWORD PLATFORM_ID = 0x04000000 | 0x00010000;      // CLIENT_OS_ID_WINNT_POST_52 | CLIENT_IMAGE_ID_MICROSOFT
    constexpr DWORD RSA1_MAGIC = 0x31415352;                    // "RSA1"

    // ------------------------------------------------------------------ little-endian reader / writer

    struct Reader
    {
        const BYTE* p;
        size_t left;
        bool ok = true;

        bool Take(void* out, size_t n)
        {
            if (!ok || left < n) {
                ok = false;
                return false;
            }
            if (out) {
                memcpy(out, p, n);
            }
            p += n;
            left -= n;
            return true;
        }
        WORD U16() { WORD v = 0; Take(&v, 2); return v; }
        DWORD U32() { DWORD v = 0; Take(&v, 4); return v; }
        // LICENSE_BINARY_BLOB: wBlobType, wBlobLen, blobData
        const BYTE* Blob(WORD* type, WORD* length)
        {
            *type = U16();
            *length = U16();
            const BYTE* data = p;
            return Take(nullptr, *length) ? data : nullptr;
        }
    };

    void Put16(std::vector<BYTE>& v, WORD x) { v.push_back(static_cast<BYTE>(x)); v.push_back(static_cast<BYTE>(x >> 8)); }
    void Put32(std::vector<BYTE>& v, DWORD x) { Put16(v, static_cast<WORD>(x)); Put16(v, static_cast<WORD>(x >> 16)); }
    void PutBytes(std::vector<BYTE>& v, const void* data, size_t n)
    {
        const BYTE* b = static_cast<const BYTE*>(data);
        v.insert(v.end(), b, b + n);
    }
    void PutBlob(std::vector<BYTE>& v, WORD type, const void* data, size_t n)
    {
        Put16(v, type);
        Put16(v, static_cast<WORD>(n));
        PutBytes(v, data, n);
    }
    void FinishPreamble(std::vector<BYTE>& v, BYTE type)
    {
        v[0] = type;
        v[1] = PREAMBLE_FLAGS;
        v[2] = static_cast<BYTE>(v.size());
        v[3] = static_cast<BYTE>(v.size() >> 8);
    }

    // ------------------------------------------------------------------ crypto (Windows CNG)

    struct Part
    {
        const void* data;
        size_t size;
    };

    bool Hash(BCRYPT_ALG_HANDLE alg, std::initializer_list<Part> parts, BYTE* out, ULONG outSize)
    {
        BCRYPT_HASH_HANDLE hash = nullptr;
        if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) != 0) {
            return false;
        }
        bool ok = true;
        for (const Part& part : parts) {
            ok = ok && BCryptHashData(hash, static_cast<PUCHAR>(const_cast<void*>(part.data)), static_cast<ULONG>(part.size), 0) == 0;
        }
        ok = ok && BCryptFinishHash(hash, out, outSize, 0) == 0;
        BCryptDestroyHash(hash);
        return ok;
    }

    // [MS-RDPBCGR] 5.3.5.1: SaltedHash(S, I) = MD5(S + SHA(I + S + R1 + R2))
    void SaltedHash(const BYTE* secret48, const char* salt, const BYTE* r1, const BYTE* r2, BYTE out[16])
    {
        BYTE sha[20];
        Hash(BCRYPT_SHA1_ALG_HANDLE, { { salt, strlen(salt) }, { secret48, 48 }, { r1, 32 }, { r2, 32 } }, sha, sizeof(sha));
        Hash(BCRYPT_MD5_ALG_HANDLE, { { secret48, 48 }, { sha, sizeof(sha) } }, out, 16);
    }

    void Rc4(const BYTE key[16], BYTE* data, size_t size)
    {
        BCRYPT_KEY_HANDLE handle = nullptr;
        if (BCryptGenerateSymmetricKey(BCRYPT_RC4_ALG_HANDLE, &handle, nullptr, 0, const_cast<PUCHAR>(key), 16, 0) == 0) {
            ULONG done = 0;
            BCryptEncrypt(handle, data, static_cast<ULONG>(size), nullptr, nullptr, 0, data, static_cast<ULONG>(size), &done, 0);
            BCryptDestroyKey(handle);
        }
    }

    // [MS-RDPBCGR] 5.3.6.1 with the licensing MAC salt: MD5(salt + pad2 + SHA(salt + pad1 + length + data)), 16 bytes.
    void Mac(const BYTE salt[16], const BYTE* data, size_t size, BYTE out[16])
    {
        BYTE pad1[40], pad2[48], sha[20];
        memset(pad1, 0x36, sizeof(pad1));
        memset(pad2, 0x5C, sizeof(pad2));
        DWORD length = static_cast<DWORD>(size);
        Hash(BCRYPT_SHA1_ALG_HANDLE, { { salt, 16 }, { pad1, sizeof(pad1) }, { &length, 4 }, { data, size } }, sha, sizeof(sha));
        Hash(BCRYPT_MD5_ALG_HANDLE, { { salt, 16 }, { pad2, sizeof(pad2) }, { sha, sizeof(sha) } }, out, 16);
    }

    // Raw RSA (no padding) as [MS-RDPBCGR] 5.3.4.1: little-endian input/output, result padded with 8 zero bytes.
    bool RsaEncrypt(const std::vector<BYTE>& modulusLe, DWORD exponent, const BYTE* data, size_t size, std::vector<BYTE>& out)
    {
        const size_t n = modulusLe.size();
        if (size > n) {
            return false;
        }

        BYTE expBe[4] = { static_cast<BYTE>(exponent >> 24), static_cast<BYTE>(exponent >> 16), static_cast<BYTE>(exponent >> 8), static_cast<BYTE>(exponent) };
        size_t skip = 0;
        while (skip < 3 && expBe[skip] == 0) {
            skip++;
        }
        const ULONG expSize = static_cast<ULONG>(4 - skip);

        std::vector<BYTE> blob(sizeof(BCRYPT_RSAKEY_BLOB) + expSize + n);
        auto* header = reinterpret_cast<BCRYPT_RSAKEY_BLOB*>(blob.data());
        header->Magic = BCRYPT_RSAPUBLIC_MAGIC;
        header->BitLength = static_cast<ULONG>(n * 8);
        header->cbPublicExp = expSize;
        header->cbModulus = static_cast<ULONG>(n);
        memcpy(blob.data() + sizeof(BCRYPT_RSAKEY_BLOB), expBe + skip, expSize);
        for (size_t i = 0; i < n; i++) {
            blob[sizeof(BCRYPT_RSAKEY_BLOB) + expSize + i] = modulusLe[n - 1 - i];       // to big-endian
        }

        BCRYPT_KEY_HANDLE key = nullptr;
        if (BCryptImportKeyPair(BCRYPT_RSA_ALG_HANDLE, nullptr, BCRYPT_RSAPUBLIC_BLOB, &key, blob.data(),
                static_cast<ULONG>(blob.size()), 0) != 0) {
            return false;
        }

        std::vector<BYTE> inBe(n, 0), outBe(n, 0);
        for (size_t i = 0; i < size; i++) {
            inBe[n - 1 - i] = data[i];                                                   // to big-endian
        }
        ULONG done = 0;
        NTSTATUS status = BCryptEncrypt(key, inBe.data(), static_cast<ULONG>(n), nullptr, nullptr, 0, outBe.data(),
            static_cast<ULONG>(n), &done, BCRYPT_PAD_NONE);
        BCryptDestroyKey(key);
        if (status != 0) {
            Log(L"Licensing: RSA encrypt failed 0x%08X", static_cast<unsigned>(status));
            return false;
        }

        out.assign(n + 8, 0);
        for (size_t i = 0; i < n; i++) {
            out[i] = outBe[n - 1 - i];                                                   // back to little-endian
        }
        return true;
    }
}

// ------------------------------------------------------------------ LicensingClient

LicensingClient::LicensingClient(ULONG seat) : _seat(seat)
{
    BCryptGenRandom(nullptr, _clientRandom, sizeof(_clientRandom), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    BCryptGenRandom(nullptr, _premaster, sizeof(_premaster), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
}

bool LicensingClient::Respond(const BYTE* pdu, ULONG size, std::vector<BYTE>& out)
{
    if (!pdu || size < 4) {
        return false;
    }
    const WORD length = static_cast<WORD>(pdu[2] | (pdu[3] << 8));
    const size_t bodySize = (length <= size ? length : size) - 4;
    switch (pdu[0]) {
    case LICENSE_REQUEST:
        return OnLicenseRequest(pdu + 4, bodySize, out);
    case PLATFORM_CHALLENGE:
        return OnPlatformChallenge(pdu + 4, bodySize, out);
    default:
        Receive(pdu, size);
        return false;
    }
}

void LicensingClient::Receive(const BYTE* pdu, ULONG size)
{
    if (!pdu || size < 4) {
        return;
    }
    if (pdu[0] == NEW_LICENSE || pdu[0] == UPGRADE_LICENSE) {
        OnNewLicense(pdu + 4, size - 4);
    } else if (pdu[0] == ERROR_ALERT && size >= 12) {
        DWORD code = 0, transition = 0;
        memcpy(&code, pdu + 4, 4);
        memcpy(&transition, pdu + 8, 4);
        Log(L"Licensing[%lu]: server ERROR_ALERT code 0x%02lX%s, transition %lu", _seat, code,
            code == 0x07 ? L" (STATUS_VALID_CLIENT)" : L"", transition);
    } else {
        Log(L"Licensing[%lu]: server message 0x%02X (%lu bytes) ignored", _seat, pdu[0], size);
    }
}

// [MS-RDPELE] 2.2.2.1 SERVER_LICENSE_REQUEST -> 2.2.2.2 CLIENT_NEW_LICENSE_REQUEST
bool LicensingClient::OnLicenseRequest(const BYTE* body, size_t size, std::vector<BYTE>& out)
{
    Reader r{ body, size };
    r.Take(_serverRandom, 32);
    r.U32();                                                    // ProductInfo.dwVersion
    r.Take(nullptr, r.U32());                                   // cbCompanyName + CompanyName
    r.Take(nullptr, r.U32());                                   // cbProductId + ProductId
    WORD type = 0, length = 0;
    r.Blob(&type, &length);                                     // KeyExchangeList
    const BYTE* cert = r.Blob(&type, &length);                  // ServerCertificate
    if (!r.ok || !cert || length == 0) {
        Log(L"Licensing[%lu]: licence request has no server certificate - cannot encrypt (need fAuthenticateServer)", _seat);
        return false;
    }

    // Proprietary certificate ([MS-RDPBCGR] 2.2.1.4.3.1.1): dwVersion, dwSigAlgId, dwKeyAlgId, public key blob.
    Reader c{ cert, length };
    const DWORD version = c.U32() & 0x7FFFFFFF;
    if (version != 1) {
        Log(L"Licensing[%lu]: certificate version %lu (X.509 chain) not supported yet", _seat, version);
        return false;
    }
    c.U32();                                                    // dwSigAlgId
    c.U32();                                                    // dwKeyAlgId
    WORD keyType = 0, keyLength = 0;
    const BYTE* key = c.Blob(&keyType, &keyLength);
    Reader k{ key ? key : cert, key ? keyLength : 0u };
    const DWORD magic = k.U32(), keylen = k.U32(), bitlen = k.U32();
    k.U32();                                                    // datalen
    _exponent = k.U32();
    if (!c.ok || !k.ok || magic != RSA1_MAGIC || bitlen / 8 > keylen || k.left < keylen) {
        Log(L"Licensing[%lu]: cannot read the server RSA key", _seat);
        return false;
    }
    _modulus.assign(k.p, k.p + bitlen / 8);
    Log(L"Licensing[%lu]: server key RSA %lu bit, exponent %lu", _seat, bitlen, _exponent);

    std::vector<BYTE> encrypted;
    if (!RsaEncrypt(_modulus, _exponent, _premaster, sizeof(_premaster), encrypted)) {
        return false;
    }
    DeriveKeys();

    char name[16] = {};
    const int nameLength = sprintf_s(name, "SEAT%lu", _seat);

    out.assign(4, 0);
    Put32(out, KEY_EXCHANGE_ALG_RSA);
    Put32(out, PLATFORM_ID);
    PutBytes(out, _clientRandom, sizeof(_clientRandom));
    PutBlob(out, BB_RANDOM_BLOB, encrypted.data(), encrypted.size());
    PutBlob(out, BB_CLIENT_USER_NAME_BLOB, name, static_cast<size_t>(nameLength) + 1);
    PutBlob(out, BB_CLIENT_MACHINE_NAME_BLOB, name, static_cast<size_t>(nameLength) + 1);
    FinishPreamble(out, NEW_LICENSE_REQUEST);
    Log(L"Licensing[%lu]: -> CLIENT_NEW_LICENSE_REQUEST (%zu bytes)", _seat, out.size());
    return true;
}

// [MS-RDPELE] 2.2.2.4 SERVER_PLATFORM_CHALLENGE -> 2.2.2.5 CLIENT_PLATFORM_CHALLENGE_RESPONSE
bool LicensingClient::OnPlatformChallenge(const BYTE* body, size_t size, std::vector<BYTE>& out)
{
    if (!_keysReady) {
        Log(L"Licensing[%lu]: platform challenge before key exchange", _seat);
        return false;
    }
    Reader r{ body, size };
    r.U32();                                                    // ConnectFlags
    WORD type = 0, length = 0;
    const BYTE* encrypted = r.Blob(&type, &length);
    BYTE mac[16] = {};
    r.Take(mac, sizeof(mac));
    if (!r.ok || !encrypted) {
        Log(L"Licensing[%lu]: bad platform challenge", _seat);
        return false;
    }

    std::vector<BYTE> challenge(encrypted, encrypted + length);
    Rc4(_licenseKey, challenge.data(), challenge.size());
    BYTE expected[16];
    Mac(_macSalt, challenge.data(), challenge.size(), expected);
    Log(L"Licensing[%lu]: platform challenge %u bytes, MAC %s", _seat, length,
        memcmp(expected, mac, 16) == 0 ? L"ok (keys match the server)" : L"MISMATCH (key derivation wrong?)");

    // PLATFORM_CHALLENGE_RESPONSE_DATA: wVersion, wClientType, wLicenseDetailLevel, cbChallenge, pbChallenge
    std::vector<BYTE> response;
    Put16(response, 0x0100);
    Put16(response, 0xFF00);                                    // OTHER_PLATFORM_CHALLENGE_TYPE
    Put16(response, 0x0003);                                    // LICENSE_DETAIL_DETAIL
    Put16(response, static_cast<WORD>(challenge.size()));
    PutBytes(response, challenge.data(), challenge.size());

    // CLIENT_HARDWARE_ID: PlatformId + 16 bytes, stable per seat.
    std::vector<BYTE> hwid;
    Put32(hwid, PLATFORM_ID);
    for (DWORD i = 0; i < 4; i++) {
        Put32(hwid, 0x4D530000u | (_seat << 8) | i);            // "MS" + seat + index
    }

    std::vector<BYTE> plain(response);
    plain.insert(plain.end(), hwid.begin(), hwid.end());
    BYTE responseMac[16];
    Mac(_macSalt, plain.data(), plain.size(), responseMac);

    Rc4(_licenseKey, response.data(), response.size());
    Rc4(_licenseKey, hwid.data(), hwid.size());

    out.assign(4, 0);
    PutBlob(out, BB_ENCRYPTED_DATA_BLOB, response.data(), response.size());
    PutBlob(out, BB_ENCRYPTED_DATA_BLOB, hwid.data(), hwid.size());
    PutBytes(out, responseMac, sizeof(responseMac));
    FinishPreamble(out, PLATFORM_CHALLENGE_RESPONSE);
    Log(L"Licensing[%lu]: -> CLIENT_PLATFORM_CHALLENGE_RESPONSE (%zu bytes)", _seat, out.size());
    return true;
}

// [MS-RDPELE] 2.2.2.6 SERVER_NEW_LICENSE / 2.2.2.7 SERVER_UPGRADE_LICENSE
void LicensingClient::OnNewLicense(const BYTE* body, size_t size)
{
    Reader r{ body, size };
    WORD type = 0, length = 0;
    const BYTE* encrypted = r.Blob(&type, &length);
    BYTE mac[16] = {};
    r.Take(mac, sizeof(mac));
    if (!r.ok || !encrypted || !_keysReady) {
        Log(L"Licensing[%lu]: bad new-licence message", _seat);
        return;
    }
    _license.assign(encrypted, encrypted + length);
    Rc4(_licenseKey, _license.data(), _license.size());
    BYTE expected[16];
    Mac(_macSalt, _license.data(), _license.size(), expected);
    Log(L"Licensing[%lu]: received a licence (%u bytes), MAC %s", _seat, length, memcmp(expected, mac, 16) == 0 ? L"ok" : L"MISMATCH");
}

// [MS-RDPBCGR] 5.3.5.1 + [MS-RDPELE] 5.1.3: master secret, session key blob, MAC salt, licensing encryption key.
void LicensingClient::DeriveKeys()
{
    BYTE master[48], sessionBlob[48];
    const char* salts[3] = { "A", "BB", "CCC" };
    for (int i = 0; i < 3; i++) {
        SaltedHash(_premaster, salts[i], _clientRandom, _serverRandom, master + 16 * i);
    }
    for (int i = 0; i < 3; i++) {
        SaltedHash(master, salts[i], _serverRandom, _clientRandom, sessionBlob + 16 * i);
    }
    memcpy(_macSalt, sessionBlob, 16);
    Hash(BCRYPT_MD5_ALG_HANDLE, { { sessionBlob + 16, 16 }, { _clientRandom, 32 }, { _serverRandom, 32 } }, _licenseKey, 16);
    SecureZeroMemory(master, sizeof(master));
    SecureZeroMemory(sessionBlob, sizeof(sessionBlob));
    _keysReady = true;
}
