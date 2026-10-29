## Short description

AES-GCM encryption (NIST SP 800-38D) for an IV of any length, composed from the platform's AES-ECB
block cipher (AVO-4069). Needed because the Inspector's encrypted-value format uses a 16-byte IV and
`System.Security.Cryptography.AesGcm` only accepts 12-byte nonces.

## Functional requirements

- `Encrypt(key, iv, plaintext)` → `(ciphertext, 16-byte tag)`, no associated data. Empty IV throws.
- H = E_K(0¹²⁸). J0 = IV ‖ 0³¹ ‖ 1 for a 12-byte IV, else GHASH_H(IV ‖ zero pad ‖ [0]₆₄ ‖ [len(IV)·8]₆₄).
- C = GCTR_K(inc32(J0), P) (keystream from one ECB transform of all counter blocks).
- T = E_K(J0) ⊕ GHASH_H(C ‖ zero pad ‖ [0]₆₄ ‖ [len(C)·8]₆₄).
- GF(2¹²⁸) multiplication per SP 800-38D Algorithm 1 (R = 0xE1 ‖ 0¹²⁰), branch-free on the operand bits.

## Non-functional requirements

Verified in tests against `AesGcm` for 12-byte nonces and against Node's `aes-256-gcm` for 16-byte
IVs. Encryption only; the SDK never decrypts.
