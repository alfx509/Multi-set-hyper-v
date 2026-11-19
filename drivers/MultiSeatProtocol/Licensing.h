// Licensing.h - client side of the RDP Licensing Extension ([MS-RDPELE]) for the MultiSeat Local provider.
//
// Remote Desktop Services runs the server side of RD licensing and talks to the protocol provider through
// IWRdsProtocolLicenseConnection: RequestClientLicense(server PDU) -> client PDU, SendClientLicense(server PDU).
// A local seat has no RDP client, so the provider answers as the client:
//   SERVER_LICENSE_REQUEST  -> CLIENT_NEW_LICENSE_REQUEST          (RSA-encrypted premaster secret)
//   SERVER_PLATFORM_CHALLENGE -> CLIENT_PLATFORM_CHALLENGE_RESPONSE (RC4 + MAC with the licensing keys)
//   SERVER_NEW_LICENSE / UPGRADE_LICENSE -> decrypted and kept
//   ERROR_ALERT (e.g. STATUS_VALID_CLIENT) -> logged
// Implemented from the published Open Specification [MS-RDPELE] (and the key derivation in [MS-RDPBCGR] 5.3.5).
#pragma once

#include <windows.h>
#include <vector>

class LicensingClient
{
public:
    explicit LicensingClient(ULONG seat);

    // Builds the client's answer to a server PDU. Returns false if the PDU needs no answer or is not understood.
    bool Respond(const BYTE* serverPdu, ULONG size, std::vector<BYTE>& clientPdu);

    // A server PDU that needs no answer (new licence, error alert).
    void Receive(const BYTE* serverPdu, ULONG size);

    bool HasLicense() const { return !_license.empty(); }

private:
    bool OnLicenseRequest(const BYTE* body, size_t size, std::vector<BYTE>& out);
    bool OnPlatformChallenge(const BYTE* body, size_t size, std::vector<BYTE>& out);
    void OnNewLicense(const BYTE* body, size_t size);
    void DeriveKeys();

    ULONG _seat;
    BYTE _clientRandom[32] = {};
    BYTE _serverRandom[32] = {};
    BYTE _premaster[48] = {};
    BYTE _macSalt[16] = {};
    BYTE _licenseKey[16] = {};
    std::vector<BYTE> _modulus;     // little-endian, without the 8 zero bytes of padding
    DWORD _exponent = 0;
    bool _keysReady = false;
    std::vector<BYTE> _license;
};
