using System;
using System.Security.Cryptography;

namespace Avo.Inspector.Internal
{
    /// <summary>
    /// AES-GCM encryption (NIST SP 800-38D) for an IV of any length, built on the platform's AES
    /// block cipher.
    /// </summary>
    /// <remarks>
    /// The Inspector's encrypted-value format uses a 16-byte IV, as the web, Node and Android SDKs
    /// write it and the Debugger reads it. <see cref="System.Security.Cryptography"/>'s
    /// <c>AesGcm</c> only accepts a 12-byte nonce (and does not exist on netstandard2.0), so GCM is
    /// composed here from AES-ECB block encryptions: the GHASH-derived pre-counter block for a
    /// non-96-bit IV, the CTR keystream, and the GHASH tag. The tests check it against
    /// <c>AesGcm</c> for 12-byte nonces and against Node's <c>aes-256-gcm</c> for 16-byte IVs.
    /// Only encryption is needed: the SDK never decrypts.
    /// </remarks>
    internal static class AesGcmAnyIv
    {
        private const int BlockSize = 16;
        internal const int TagSize = 16;

        /// <summary>Encrypts <paramref name="plaintext"/> with no associated data.</summary>
        /// <returns>The ciphertext (same length as the plaintext) and the 16-byte tag.</returns>
        public static (byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, byte[] iv, byte[] plaintext)
        {
            if (iv.Length == 0)
            {
                throw new ArgumentException("IV must not be empty.", nameof(iv));
            }
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = key;
                using (var block = aes.CreateEncryptor())
                {
                    // H = E_K(0^128)
                    var h = EncryptBlock(block, new byte[BlockSize]);
                    var hHi = ReadUInt64(h, 0);
                    var hLo = ReadUInt64(h, 8);

                    var j0 = PreCounterBlock(iv, hHi, hLo);

                    // C = GCTR_K(inc32(J0), P)
                    var counter = (byte[])j0.Clone();
                    Increment32(counter);
                    var ciphertext = Ctr(block, counter, plaintext);

                    // S = GHASH_H(C || 0^v || [len(A)]_64 || [len(C)]_64), with A empty.
                    ulong sHi = 0, sLo = 0;
                    GhashUpdate(ref sHi, ref sLo, hHi, hLo, ciphertext);
                    sLo ^= (ulong)ciphertext.Length * 8;
                    GfMultiply(ref sHi, ref sLo, hHi, hLo);

                    // T = E_K(J0) xor S
                    var tag = EncryptBlock(block, j0);
                    Xor(tag, 0, sHi);
                    Xor(tag, 8, sLo);
                    return (ciphertext, tag);
                }
            }
        }

        // J0 = IV || 0^31 || 1 for a 96-bit IV, else GHASH_H(IV || 0^(s+64) || [len(IV)]_64).
        private static byte[] PreCounterBlock(byte[] iv, ulong hHi, ulong hLo)
        {
            var j0 = new byte[BlockSize];
            if (iv.Length == 12)
            {
                Buffer.BlockCopy(iv, 0, j0, 0, 12);
                j0[15] = 1;
                return j0;
            }
            ulong yHi = 0, yLo = 0;
            GhashUpdate(ref yHi, ref yLo, hHi, hLo, iv);
            yLo ^= (ulong)iv.Length * 8;
            GfMultiply(ref yHi, ref yLo, hHi, hLo);
            WriteUInt64(j0, 0, yHi);
            WriteUInt64(j0, 8, yLo);
            return j0;
        }

        private static byte[] Ctr(ICryptoTransform block, byte[] counter, byte[] input)
        {
            var output = new byte[input.Length];
            if (input.Length == 0)
            {
                return output;
            }
            var blocks = (input.Length + BlockSize - 1) / BlockSize;
            var counters = new byte[blocks * BlockSize];
            for (var i = 0; i < blocks; i++)
            {
                Buffer.BlockCopy(counter, 0, counters, i * BlockSize, BlockSize);
                Increment32(counter);
            }
            var keystream = new byte[counters.Length];
            block.TransformBlock(counters, 0, counters.Length, keystream, 0);
            for (var i = 0; i < input.Length; i++)
            {
                output[i] = (byte)(input[i] ^ keystream[i]);
            }
            return output;
        }

        // Absorbs data into the GHASH state, zero-padding the last block.
        private static void GhashUpdate(ref ulong yHi, ref ulong yLo, ulong hHi, ulong hLo, byte[] data)
        {
            var padded = new byte[BlockSize];
            for (var offset = 0; offset < data.Length; offset += BlockSize)
            {
                var length = Math.Min(BlockSize, data.Length - offset);
                Array.Clear(padded, 0, BlockSize);
                Buffer.BlockCopy(data, offset, padded, 0, length);
                yHi ^= ReadUInt64(padded, 0);
                yLo ^= ReadUInt64(padded, 8);
                GfMultiply(ref yHi, ref yLo, hHi, hLo);
            }
        }

        // X = X • Y in GF(2^128) with GCM's bit order (SP 800-38D, Algorithm 1). Branch-free on the
        // bits of X and Y.
        private static void GfMultiply(ref ulong xHi, ref ulong xLo, ulong yHi, ulong yLo)
        {
            ulong zHi = 0, zLo = 0, vHi = yHi, vLo = yLo;
            for (var i = 0; i < 128; i++)
            {
                var bit = i < 64 ? (xHi >> (63 - i)) & 1 : (xLo >> (127 - i)) & 1;
                var mask = 0UL - bit;
                zHi ^= vHi & mask;
                zLo ^= vLo & mask;
                var carry = 0UL - (vLo & 1);
                vLo = (vLo >> 1) | (vHi << 63);
                vHi = (vHi >> 1) ^ (0xE100000000000000UL & carry);
            }
            xHi = zHi;
            xLo = zLo;
        }

        private static byte[] EncryptBlock(ICryptoTransform block, byte[] input)
        {
            var output = new byte[BlockSize];
            block.TransformBlock(input, 0, BlockSize, output, 0);
            return output;
        }

        // inc32: increments the last 32 bits, big-endian, modulo 2^32.
        private static void Increment32(byte[] counter)
        {
            for (var i = BlockSize - 1; i >= BlockSize - 4; i--)
            {
                if (++counter[i] != 0)
                {
                    return;
                }
            }
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++)
            {
                value = (value << 8) | buffer[offset + i];
            }
            return value;
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (var i = 7; i >= 0; i--)
            {
                buffer[offset + i] = (byte)value;
                value >>= 8;
            }
        }

        private static void Xor(byte[] buffer, int offset, ulong value)
        {
            for (var i = 7; i >= 0; i--)
            {
                buffer[offset + i] ^= (byte)value;
                value >>= 8;
            }
        }
    }
}
