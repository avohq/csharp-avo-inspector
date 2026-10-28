---
import: src/AvoInspector/Internal/AesGcmAnyIv.cs.md
---

## Short description

ECIES encryption of property values for the Inspector Debugger (AVO-4069), byte-compatible with the
web and Node SDKs: `base64([0x00][65-byte uncompressed ephemeral public key][16-byte IV][16-byte GCM
tag][ciphertext])`, P-256 ECDH, AES key = SHA-256 of the shared secret's X coordinate, AES-256-GCM.

## Functional requirements

- `TryCreate(publicKeyHex, out error)`: trims the key and drops a `0x`/`0X` prefix (as the web and
  Android SDKs accept), then accepts an uncompressed (`04…`, 130 hex) or compressed
  (`02…`/`03…`, 66 hex) P-256 key, decompressing with `y = rhs^((p+1)/4) mod p` and the prefix's
  parity. Performs one trial derivation, so an off-curve key or a platform without P-256 ECDH fails
  here. Returns `null` with an `error` that never contains the key. On the netstandard2.0 build
  (no `ECDiffieHellman`) it always returns `null` ("needs .NET 8 or later").
- `Encrypt(plaintext)` (net8.0): fresh ephemeral key pair per call
  (`ECDiffieHellman.Create(nistP256)`), `DeriveKeyFromHash(recipient, SHA256)` (= SHA-256(Z)), a fresh
  random 16-byte IV, `AesGcmAnyIv.Encrypt` over the UTF-8 plaintext, assembled as above. Throws on
  failure; the caller omits the value.

## Non-functional requirements

Thread-safe: the only shared state is the parsed recipient key and a locked
`RandomNumberGenerator`. The derived AES key is cleared after use.
