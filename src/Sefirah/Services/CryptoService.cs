using System.Security.Cryptography;
using System.Text;

namespace Sefirah.Services;

public static class CryptoService
{
    // 使用 HKDF-SHA256 从两个公钥（按字典序）派生 32 字节对称密钥，返回 Base64 编码
    public static string GenerateSharedSecret(string localKey, string remoteKey)
    {
        string key1 = string.Compare(localKey, remoteKey) < 0 ? localKey : remoteKey;
        string key2 = string.Compare(localKey, remoteKey) < 0 ? remoteKey : localKey;
        string combined = key1 + key2;
        byte[] ikm = Encoding.UTF8.GetBytes(combined);

        // HKDF-Extract with empty salt (all-zero salt per RFC if none provided)
        byte[] salt = new byte[32];
        byte[] prk;
        using (var hmac = new HMACSHA256(salt))
        {
            prk = hmac.ComputeHash(ikm);
        }

        // HKDF-Expand to 32 bytes, using the same info string as Android: "shared-secret"
        byte[] okm = new byte[32];
        byte[] previous = new byte[0];
        byte counter = 1;
        byte[] infoBytes = Encoding.UTF8.GetBytes("shared-secret");
        using (var hmac = new HMACSHA256(prk))
        using (var ms = new MemoryStream())
        {
            while (ms.Length < okm.Length)
            {
                hmac.Initialize();
                if (previous.Length > 0)
                {
                    hmac.TransformBlock(previous, 0, previous.Length, previous, 0);
                }
                if (infoBytes.Length > 0)
                {
                    hmac.TransformBlock(infoBytes, 0, infoBytes.Length, infoBytes, 0);
                }
                hmac.TransformFinalBlock(new byte[] { counter }, 0, 1);
                byte[] t = hmac.Hash!;
                if (t.Length > 0)
                {
                    ms.Write(t, 0, t.Length);
                }
                previous = t;
                counter++;
            }
            Array.Copy(ms.ToArray(), okm, okm.Length);
        }

        return Convert.ToBase64String(okm);
    }

    public static string Encrypt(string plainText, string key)
    {
        try
        {
            byte[] keyBytes = Convert.FromBase64String(key);
            byte[] plaintextBytes = Encoding.UTF8.GetBytes(plainText);

            // 使用 AES-GCM，12 字节随机 IV，标签长度 16
            byte[] iv = new byte[12];
            RandomNumberGenerator.Fill(iv);

            byte[] cipherText = new byte[plaintextBytes.Length];
            byte[] tag = new byte[16];

            using (var aesgcm = new AesGcm(keyBytes))
            {
                aesgcm.Encrypt(iv, plaintextBytes, cipherText, tag, null);
            }

            // 输出格式: iv || ciphertext || tag
            byte[] outBuf = new byte[iv.Length + cipherText.Length + tag.Length];
            Buffer.BlockCopy(iv, 0, outBuf, 0, iv.Length);
            Buffer.BlockCopy(cipherText, 0, outBuf, iv.Length, cipherText.Length);
            Buffer.BlockCopy(tag, 0, outBuf, iv.Length + cipherText.Length, tag.Length);

            return Convert.ToBase64String(outBuf);
        }
        catch (Exception ex)
        {
            // 记录错误日志
            // logger.LogError("Encryption failed: {ex}", ex);
            return plainText;
        }
    }

    public static string Decrypt(string encryptedText, string key)
    {
        try
        {
            byte[] keyBytes = Convert.FromBase64String(key);
            byte[] inBuf = Convert.FromBase64String(encryptedText);

            if (inBuf.Length < 12 + 16)
            {
                throw new ArgumentException("加密数据长度不足");
            }

            byte[] iv = new byte[12];
            Buffer.BlockCopy(inBuf, 0, iv, 0, iv.Length);
            int cipherLen = inBuf.Length - iv.Length - 16;
            byte[] cipherText = new byte[cipherLen];
            Buffer.BlockCopy(inBuf, iv.Length, cipherText, 0, cipherLen);
            byte[] tag = new byte[16];
            Buffer.BlockCopy(inBuf, iv.Length + cipherLen, tag, 0, tag.Length);

            byte[] plainBytes = new byte[cipherLen];
            using (var aesgcm = new AesGcm(keyBytes))
            {
                aesgcm.Decrypt(iv, cipherText, tag, plainBytes, null);
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            // 记录错误日志
            // logger.LogError("Decryption failed: {ex}", ex);
            return encryptedText;
        }
    }
}