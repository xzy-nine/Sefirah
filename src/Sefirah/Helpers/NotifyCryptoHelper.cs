using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace Sefirah.Helpers;

/// <summary>
/// Notify 协议加解密与密钥派生工具。
/// </summary>
public static class NotifyCryptoHelper
{
    private const int SharedSecretLength = 32;

    public static string GeneratePublicKey()
    {
        return Guid.NewGuid().ToString().Replace("-", "");
    }

    public static string NormalizePublicKey(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) && key.Length == 32 && Regex.IsMatch(key, "^[0-9a-fA-F]{32}$"))
        {
            return key;
        }

        return GeneratePublicKey();
    }

    public static byte[] GenerateSharedSecretBytes(string localKey, string remoteKey)
    {
        var (k1, k2) = string.Compare(localKey, remoteKey, StringComparison.Ordinal) < 0
            ? (localKey, remoteKey)
            : (remoteKey, localKey);

        var ikm = Encoding.UTF8.GetBytes(k1 + k2);

        Span<byte> salt = stackalloc byte[SharedSecretLength];
        byte[] prk;
        using (var hmac = new HMACSHA256(salt.ToArray()))
        {
            prk = hmac.ComputeHash(ikm);
        }

        byte[] okm = new byte[SharedSecretLength];
        byte[] previous = Array.Empty<byte>();
        byte counter = 1;
        byte[] infoBytes = Encoding.UTF8.GetBytes("shared-secret");

        using var hmacExpand = new HMACSHA256(prk);
        using var ms = new MemoryStream();
        while (ms.Length < SharedSecretLength)
        {
            hmacExpand.Initialize();
            if (previous.Length > 0)
            {
                hmacExpand.TransformBlock(previous, 0, previous.Length, previous, 0);
            }
            if (infoBytes.Length > 0)
            {
                hmacExpand.TransformBlock(infoBytes, 0, infoBytes.Length, infoBytes, 0);
            }
            hmacExpand.TransformFinalBlock(new byte[] { counter }, 0, 1);
            var t = hmacExpand.Hash ?? Array.Empty<byte>();
            if (t.Length > 0)
            {
                ms.Write(t, 0, t.Length);
            }
            previous = t;
            counter++;
        }

        Array.Copy(ms.ToArray(), okm, okm.Length);
        return okm;
    }

    public static string GenerateSharedSecretBase64(string localKey, string remoteKey)
    {
        return Convert.ToBase64String(GenerateSharedSecretBytes(localKey, remoteKey));
    }

    public static string Encrypt(string plainText, byte[] sharedSecret)
    {
        try
        {
            byte[] keyBytes = sharedSecret;
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(plainText);

            byte[] iv = new byte[12];
            RandomNumberGenerator.Fill(iv);

            byte[] cipherText = new byte[plaintextBytes.Length];
            byte[] tag = new byte[16];

            using (var aesgcm = new AesGcm(keyBytes))
            {
                aesgcm.Encrypt(iv, plaintextBytes, cipherText, tag, null);
            }

            byte[] output = new byte[iv.Length + cipherText.Length + tag.Length];
            Buffer.BlockCopy(iv, 0, output, 0, iv.Length);
            Buffer.BlockCopy(cipherText, 0, output, iv.Length, cipherText.Length);
            Buffer.BlockCopy(tag, 0, output, iv.Length + cipherText.Length, tag.Length);

            return Convert.ToBase64String(output);
        }
        catch
        {
            return plainText;
        }
    }

    public static string Decrypt(string encryptedText, byte[] sharedSecret)
    {
        try
        {
            byte[] keyBytes = sharedSecret;
            byte[] buffer = Convert.FromBase64String(encryptedText);

            if (buffer.Length < 28)
            {
                throw new ArgumentException("Invalid encrypted payload length");
            }

            byte[] iv = new byte[12];
            Buffer.BlockCopy(buffer, 0, iv, 0, iv.Length);

            int cipherLen = buffer.Length - iv.Length - 16;
            byte[] cipherText = new byte[cipherLen];
            Buffer.BlockCopy(buffer, iv.Length, cipherText, 0, cipherLen);

            byte[] tag = new byte[16];
            Buffer.BlockCopy(buffer, iv.Length + cipherLen, tag, 0, tag.Length);

            byte[] plainBytes = new byte[cipherLen];
            using (var aesgcm = new AesGcm(keyBytes))
            {
                aesgcm.Decrypt(iv, cipherText, tag, plainBytes, null);
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return encryptedText;
        }
    }

    public static byte[] ComputePasskey(byte[] sharedSecret)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(sharedSecret);
    }
}