using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// Encrypts property values for the Inspector Debugger with ECIES on P-256, the format the web
    /// and Node SDKs write and the Debugger decrypts with the matching private key:
    /// <c>base64([0x00][65-byte ephemeral public key][16-byte IV][16-byte GCM tag][ciphertext])</c>,
    /// where the AES-256-GCM key is SHA-256 of the ECDH shared secret's X coordinate.
    /// </summary>
    /// <remarks>
    /// The recipient key is the hex public key from the Avo workspace: uncompressed (130 hex
    /// characters, <c>04…</c>) or compressed (66, <c>02…</c>/<c>03…</c>). Every value gets a fresh
    /// ephemeral key pair and IV. Thread-safe: no state is shared between calls but the parsed key.
    /// </remarks>
    internal sealed class ValueEncryptor
    {
        private const byte FormatVersion = 0x00;
        private const int IvSize = 16;

        // NIST P-256 (secp256r1) field prime and curve coefficient b; a = -3.
        private static readonly BigInteger P = ParseHex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
        private static readonly BigInteger B = ParseHex("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");

        private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

        private readonly ECParameters _recipient;

        private ValueEncryptor(ECParameters recipient)
        {
            _recipient = recipient;
        }

        /// <summary>
        /// Parses <paramref name="publicKeyHex"/> and checks the platform can use it. Returns
        /// <c>null</c> when the key is not a P-256 public key or this platform has no P-256 ECDH;
        /// <paramref name="error"/> then names the fault (never the key).
        /// </summary>
        public static ValueEncryptor? TryCreate(string publicKeyHex, out string? error)
        {
#if !NET8_0_OR_GREATER
            // netstandard2.0 has no ECDiffieHellman, so only the net8.0 build (an app running on
            // .NET 8 or later) can encrypt. Values are then simply not sent; validation still runs.
            error = "publicEncryptionKey is ignored: encrypting values needs .NET 8 or later";
            return null;
#else
            error = null;
            var hex = publicKeyHex.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                // The web and Android SDKs accept a 0x-prefixed key too.
                hex = hex.Substring(2);
            }
            byte[] point;
            try
            {
                point = FromHex(hex);
            }
            catch (FormatException)
            {
                error = "publicEncryptionKey is not hex";
                return null;
            }

            byte[] x, y;
            if (point.Length == 65 && point[0] == 0x04)
            {
                x = Slice(point, 1, 32);
                y = Slice(point, 33, 32);
            }
            else if (point.Length == 33 && (point[0] == 0x02 || point[0] == 0x03))
            {
                x = Slice(point, 1, 32);
                var decompressed = DecompressY(x, point[0] == 0x03);
                if (decompressed == null)
                {
                    error = "publicEncryptionKey is not a point on P-256";
                    return null;
                }
                y = decompressed;
            }
            else
            {
                error = "publicEncryptionKey does not look like a P-256 public key (expected 66 or 130 hex characters)";
                return null;
            }

            var recipient = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y }
            };
            var encryptor = new ValueEncryptor(recipient);
            try
            {
                // One trial derivation, so a key the platform rejects (not on the curve) or a platform
                // without P-256 ECDH is reported once here, not on every event.
                encryptor.DeriveKey(out _).Dispose();
            }
            catch (Exception ex) when (ex is CryptographicException || ex is PlatformNotSupportedException || ex is ArgumentException)
            {
                error = "publicEncryptionKey cannot be used for encryption on this platform (" + ex.GetType().Name + ")";
                return null;
            }
            return encryptor;
#endif
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Encrypts <paramref name="plaintext"/> (a value's JSON) and returns the base64 wire string.
        /// Throws <see cref="CryptographicException"/> on failure.
        /// </summary>
        public string Encrypt(string plaintext)
        {
            byte[] aesKey;
            byte[] ephemeralPublicKey;
            using (var ephemeral = DeriveKey(out aesKey))
            {
                var q = ephemeral.ExportParameters(false).Q;
                ephemeralPublicKey = new byte[65];
                ephemeralPublicKey[0] = 0x04;
                Buffer.BlockCopy(q.X!, 0, ephemeralPublicKey, 1 + 32 - q.X!.Length, q.X.Length);
                Buffer.BlockCopy(q.Y!, 0, ephemeralPublicKey, 33 + 32 - q.Y!.Length, q.Y.Length);
            }

            var iv = new byte[IvSize];
            lock (Random)
            {
                Random.GetBytes(iv);
            }
            var (ciphertext, tag) = AesGcmAnyIv.Encrypt(aesKey, iv, Encoding.UTF8.GetBytes(plaintext));
            Array.Clear(aesKey, 0, aesKey.Length);

            var wire = new byte[1 + ephemeralPublicKey.Length + IvSize + AesGcmAnyIv.TagSize + ciphertext.Length];
            var offset = 0;
            wire[offset++] = FormatVersion;
            Buffer.BlockCopy(ephemeralPublicKey, 0, wire, offset, ephemeralPublicKey.Length);
            offset += ephemeralPublicKey.Length;
            Buffer.BlockCopy(iv, 0, wire, offset, IvSize);
            offset += IvSize;
            Buffer.BlockCopy(tag, 0, wire, offset, AesGcmAnyIv.TagSize);
            offset += AesGcmAnyIv.TagSize;
            Buffer.BlockCopy(ciphertext, 0, wire, offset, ciphertext.Length);
            return Convert.ToBase64String(wire);
        }

        // A fresh ephemeral key pair, and the AES key SHA-256(x) of its shared secret with the recipient.
        private ECDiffieHellman DeriveKey(out byte[] aesKey)
        {
            var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                using (var recipient = ECDiffieHellman.Create(_recipient))
                {
                    // DeriveKeyFromHash with no prepend or append is hash(Z): SHA-256 of the raw X coordinate.
                    aesKey = ephemeral.DeriveKeyFromHash(recipient.PublicKey, HashAlgorithmName.SHA256);
                }
                return ephemeral;
            }
            catch
            {
                ephemeral.Dispose();
                throw;
            }
        }

#else
        public string Encrypt(string plaintext) => throw new PlatformNotSupportedException();
#endif

        // y = sqrt(x^3 - 3x + b) mod p, with the requested parity. P-256's p ≡ 3 (mod 4), so the
        // square root is rhs^((p+1)/4). Null when x has no point on the curve.
        private static byte[]? DecompressY(byte[] xBytes, bool odd)
        {
            var x = FromBigEndian(xBytes);
            if (x >= P)
            {
                return null;
            }
            var rhs = Mod(BigInteger.ModPow(x, 3, P) - 3 * x + B, P);
            var y = BigInteger.ModPow(rhs, (P + 1) / 4, P);
            if (BigInteger.ModPow(y, 2, P) != rhs)
            {
                return null;
            }
            if (!y.IsEven != odd)
            {
                y = P - y;
            }
            return ToBigEndian32(y);
        }

        private static BigInteger Mod(BigInteger value, BigInteger modulus)
        {
            var r = BigInteger.Remainder(value, modulus);
            return r.Sign < 0 ? r + modulus : r;
        }

        private static BigInteger ParseHex(string hex) => FromBigEndian(FromHex(hex));

        private static BigInteger FromBigEndian(byte[] bytes)
        {
            // BigInteger reads little-endian two's complement: reverse, and add a zero sign byte.
            var little = new byte[bytes.Length + 1];
            for (var i = 0; i < bytes.Length; i++)
            {
                little[i] = bytes[bytes.Length - 1 - i];
            }
            return new BigInteger(little);
        }

        private static byte[] ToBigEndian32(BigInteger value)
        {
            var little = value.ToByteArray();
            var result = new byte[32];
            for (var i = 0; i < 32 && i < little.Length; i++)
            {
                result[31 - i] = little[i];
            }
            return result;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(source, offset, result, 0, length);
            return result;
        }

        internal static byte[] FromHex(string hex)
        {
            if (hex.Length % 2 != 0)
            {
                throw new FormatException("Odd-length hex string.");
            }
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            }
            return bytes;
        }
    }
}
