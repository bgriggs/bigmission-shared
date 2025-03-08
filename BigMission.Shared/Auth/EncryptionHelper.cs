using System.Security.Cryptography;
using System.Text;

namespace BigMission.Shared.Auth;

public static class EncryptionHelper
{
    public static string EncryptString(string plainText, string key)
    {
        using Aes aesAlg = Aes.Create();
        aesAlg.Key = Encoding.UTF8.GetBytes(key);
        aesAlg.IV = new byte[16]; // Initialization vector with zeros

        ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

        using MemoryStream msEncrypt = new();
        using CryptoStream csEncrypt = new(msEncrypt, encryptor, CryptoStreamMode.Write);
        using StreamWriter swEncrypt = new(csEncrypt);
        swEncrypt.Write(plainText);
        return Convert.ToBase64String(msEncrypt.ToArray());
    }

    public static string DecryptString(string cipherText, string key)
    {
        using Aes aesAlg = Aes.Create();
        aesAlg.Key = Encoding.UTF8.GetBytes(key);
        aesAlg.IV = new byte[16]; // Initialization vector with zeros

        ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

        using MemoryStream msDecrypt = new(Convert.FromBase64String(cipherText));
        using CryptoStream csDecrypt = new(msDecrypt, decryptor, CryptoStreamMode.Read);
        using StreamReader srDecrypt = new(csDecrypt);
        return srDecrypt.ReadToEnd();
    }
}
