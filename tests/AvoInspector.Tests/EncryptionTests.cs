using System;
using System.Security.Cryptography;
using System.Text;
using Avo.Inspector.Internal;
using Xunit;

namespace Avo.Inspector.Tests
{
    /// <summary>
    /// Value encryption for the Inspector Debugger (AVO-4069): AES-GCM with the format's 16-byte IV,
    /// and the ECIES envelope the web and Node SDKs write. The fixed vectors were produced with
    /// Node's <c>crypto</c> (<c>aes-256-gcm</c>, <c>createECDH("prime256v1")</c>).
    /// </summary>
    public class EncryptionTests
    {
        // Private key c9af…6721 (the RFC 6979 P-256 test key) and its public key, both forms.
        private const string PrivateKeyHex = "c9afa9d845ba75166b5c215767b1d6934e50c3db36e89b127b8a622b120f6721";
        internal const string PublicKeyHex =
            "0460fed4ba255a9d31c961eb74c6356d68c049b8923b61fa6ce669622e60f29fb67903fe1008b8bc99a41ae9e95628bc64f2f1b20c2d7e9f5177a3c294d4462299";
        private const string CompressedPublicKeyHex = "0360fed4ba255a9d31c961eb74c6356d68c049b8923b61fa6ce669622e60f29fb6";

        [Fact]
        public void Gcm_with_a_16_byte_iv_matches_node()
        {
            var key = ValueEncryptor.FromHex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
            var iv = ValueEncryptor.FromHex("f0e1d2c3b4a5968778695a4b3c2d1e0f");
            var plaintext = "{\"plan\":\"Pro\",\"seats\":12,\"note\":\"vector spanning more than two AES blocks\"}";

            var (ciphertext, tag) = AesGcmAnyIv.Encrypt(key, iv, Encoding.UTF8.GetBytes(plaintext));

            Assert.Equal(
                "787f65eecad47bea9c841bb53958318ba71e2c61192f48b0551711d84cf1f9f5c19becb3cb63456950bea3c4ef059ee8ef1fffd32d2f9cefed2c2ad9e41294c04a3ab607f592f0e4d35db5",
                Convert.ToHexString(ciphertext).ToLowerInvariant());
            Assert.Equal("a933c59756061a3dc797a7ef08703034", Convert.ToHexString(tag).ToLowerInvariant());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(16)]
        [InlineData(17)]
        [InlineData(1000)]
        public void Gcm_with_a_12_byte_iv_matches_the_platform(int length)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var plaintext = RandomNumberGenerator.GetBytes(length);

            var (ciphertext, tag) = AesGcmAnyIv.Encrypt(key, nonce, plaintext);

            var expected = new byte[length];
            var expectedTag = new byte[16];
            using (var aes = new AesGcm(key, 16))
            {
                aes.Encrypt(nonce, plaintext, expected, expectedTag);
            }
            Assert.Equal(expected, ciphertext);
            Assert.Equal(expectedTag, tag);
        }

        [Theory]
        [InlineData(PublicKeyHex)]
        [InlineData(CompressedPublicKeyHex)]
        public void Encrypted_values_decrypt_with_the_private_key(string publicKeyHex)
        {
            var encryptor = ValueEncryptor.TryCreate(publicKeyHex, out var error);
            Assert.Null(error);

            var wire = encryptor!.Encrypt("\"héllo\"");

            Assert.Equal("\"héllo\"", Decrypt(wire));
        }

        [Fact]
        public void Every_value_gets_a_fresh_ephemeral_key_and_iv()
        {
            var encryptor = ValueEncryptor.TryCreate(PublicKeyHex, out _)!;
            Assert.NotEqual(encryptor.Encrypt("1"), encryptor.Encrypt("1"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("zz")]
        [InlineData("04abcd")]
        [InlineData("0560fed4ba255a9d31c961eb74c6356d68c049b8923b61fa6ce669622e60f29fb6")]
        // x = 1 has no point on P-256.
        [InlineData("020000000000000000000000000000000000000000000000000000000000000001")]
        // A point not on the curve.
        [InlineData("0460fed4ba255a9d31c961eb74c6356d68c049b8923b61fa6ce669622e60f29fb67903fe1008b8bc99a41ae9e95628bc64f2f1b20c2d7e9f5177a3c294d4462298")]
        public void Unusable_keys_are_rejected_without_echoing_them(string publicKeyHex)
        {
            Assert.Null(ValueEncryptor.TryCreate(publicKeyHex, out var error));
            Assert.NotNull(error);
            if (publicKeyHex.Length > 0)
            {
                Assert.DoesNotContain(publicKeyHex, error);
            }
        }

        /// <summary>
        /// Decrypts the wire format with <see cref="PrivateKeyHex"/>, as the Debugger does:
        /// [0x00][65-byte ephemeral key][16-byte IV][16-byte tag][ciphertext]. GCM is CTR mode plus a
        /// tag, so re-encrypting the ciphertext yields the plaintext, and re-encrypting that must
        /// reproduce the ciphertext and the tag.
        /// </summary>
        internal static string Decrypt(string base64)
        {
            var wire = Convert.FromBase64String(base64);
            Assert.Equal(0x00, wire[0]);
            Assert.Equal(0x04, wire[1]);
            var x = wire.AsSpan(2, 32).ToArray();
            var y = wire.AsSpan(34, 32).ToArray();
            var iv = wire.AsSpan(66, 16).ToArray();
            var tag = wire.AsSpan(82, 16).ToArray();
            var ciphertext = wire.AsSpan(98).ToArray();

            var publicKey = ValueEncryptor.FromHex(PublicKeyHex);
            using var recipient = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = ValueEncryptor.FromHex(PrivateKeyHex),
                Q = new ECPoint { X = publicKey.AsSpan(1, 32).ToArray(), Y = publicKey.AsSpan(33, 32).ToArray() }
            });
            using var ephemeral = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y }
            });
            var key = recipient.DeriveKeyFromHash(ephemeral.PublicKey, HashAlgorithmName.SHA256);

            var (plaintext, _) = AesGcmAnyIv.Encrypt(key, iv, ciphertext);
            var (again, expectedTag) = AesGcmAnyIv.Encrypt(key, iv, plaintext);
            Assert.Equal(ciphertext, again);
            Assert.Equal(tag, expectedTag);
            return Encoding.UTF8.GetString(plaintext);
        }
    }
}
