using System.Buffers.Binary;
using System.Security.Cryptography;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;

namespace MaSatellite.Core.Security.Noise;

public readonly record struct KeyPair(byte[] PrivateKey, byte[] PublicKey)
{
    public static KeyPair Generate() => FromPrivate(RandomNumberGenerator.GetBytes(32));

    public static KeyPair FromPrivate(byte[] privateKey)
    {
        var pub = new byte[32];
        X25519.ScalarMultBase(privateKey, 0, pub, 0);
        return new KeyPair(privateKey, pub);
    }
}

/// <summary>Primitives used by the Noise implementation: X25519, SHA-256/HMAC/HKDF and the two AEADs.</summary>
internal static class NoiseCrypto
{
    public const int HashLength = 32;
    public const int KeyLength = 32;
    public const int TagLength = 16;

    public static byte[] Dh(byte[] privateKey, byte[] publicKey)
    {
        var shared = new byte[32];
        if (!X25519.CalculateAgreement(privateKey, 0, publicKey, 0, shared, 0))
        {
            throw new CryptographicException("Invalid Diffie-Hellman public key (low-order point).");
        }

        return shared;
    }

    public static byte[] Hash(params ReadOnlyMemory<byte>[] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts)
        {
            sha.AppendData(p.Span);
        }

        return sha.GetHashAndReset();
    }

    public static byte[] Hmac(byte[] key, params ReadOnlyMemory<byte>[] parts)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        foreach (var p in parts)
        {
            h.AppendData(p.Span);
        }

        return h.GetHashAndReset();
    }

    /// <summary>Noise HKDF with two outputs.</summary>
    public static (byte[] Out1, byte[] Out2) Hkdf2(byte[] chainingKey, ReadOnlySpan<byte> inputKeyMaterial)
    {
        var temp = Hmac(chainingKey, inputKeyMaterial.ToArray());
        var o1 = Hmac(temp, new byte[] { 1 });
        var o2 = Hmac(temp, o1, new byte[] { 2 });
        return (o1, o2);
    }

    /// <summary>Noise HKDF with three outputs.</summary>
    public static (byte[] Out1, byte[] Out2, byte[] Out3) Hkdf3(byte[] chainingKey, ReadOnlySpan<byte> inputKeyMaterial)
    {
        var temp = Hmac(chainingKey, inputKeyMaterial.ToArray());
        var o1 = Hmac(temp, new byte[] { 1 });
        var o2 = Hmac(temp, o1, new byte[] { 2 });
        var o3 = Hmac(temp, o2, new byte[] { 3 });
        return (o1, o2, o3);
    }

    public static byte[] Encrypt(NoiseSuite suite, byte[] key, ulong counter, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        var nonce = Nonce(suite, counter);
        if (suite == NoiseSuite.AesGcmSha256)
        {
            using var aes = new AesGcm(key, TagLength);
            var output = new byte[plaintext.Length + TagLength];
            aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, TagLength), ad);
            return output;
        }

        var cipher = new BcChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), TagLength * 8, nonce, ad.ToArray()));
        var pt = plaintext.ToArray();
        var result = new byte[cipher.GetOutputSize(pt.Length)];
        var len = cipher.ProcessBytes(pt, 0, pt.Length, result, 0);
        cipher.DoFinal(result, len);
        return result;
    }

    public static byte[] Decrypt(NoiseSuite suite, byte[] key, ulong counter, ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagLength)
        {
            throw new CryptographicException("Ciphertext is shorter than the authentication tag.");
        }

        var nonce = Nonce(suite, counter);
        if (suite == NoiseSuite.AesGcmSha256)
        {
            using var aes = new AesGcm(key, TagLength);
            var output = new byte[ciphertext.Length - TagLength];
            aes.Decrypt(nonce, ciphertext[..^TagLength], ciphertext[^TagLength..], output, ad);
            return output;
        }

        try
        {
            var cipher = new BcChaCha20Poly1305();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), TagLength * 8, nonce, ad.ToArray()));
            var ct = ciphertext.ToArray();
            var result = new byte[cipher.GetOutputSize(ct.Length)];
            var len = cipher.ProcessBytes(ct, 0, ct.Length, result, 0);
            cipher.DoFinal(result, len);
            return result;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
        {
            throw new CryptographicException("Authentication failed.", ex);
        }
    }

    /// <summary>Noise nonce: 4 zero bytes then the 64-bit counter, little-endian for ChaChaPoly and big-endian for AESGCM.</summary>
    private static byte[] Nonce(NoiseSuite suite, ulong counter)
    {
        var nonce = new byte[12];
        if (suite == NoiseSuite.AesGcmSha256)
        {
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        }

        return nonce;
    }
}
