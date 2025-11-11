using System.Security.Cryptography;
using System.Text;

namespace BigMission.Shared.Utilities;

/// <summary>
/// Provides AES encryption and decryption services for string data.
/// </summary>
/// <remarks>
/// This service uses the AES (Advanced Encryption Standard) algorithm with a 256-bit key and 128-bit initialization vector (IV)
/// to provide symmetric encryption and decryption of string data. The encrypted data is returned as a base64-encoded string.
/// This class is thread-safe and can be reused for multiple encryption/decryption operations with the same key and IV.
/// </remarks>
public class EncryptionService
{
    private readonly byte[] key;
    private readonly byte[] iv;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncryptionService"/> class with the specified key and initialization vector.
    /// </summary>
    /// <param name="key">The encryption key as a UTF-8 string. Must be exactly 32 bytes (256 bits) long.</param>
    /// <param name="iv">The initialization vector as a UTF-8 string. Must be exactly 16 bytes (128 bits) long.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the key is not 32 bytes long or the IV is not 16 bytes long.
    /// </exception>
    /// <remarks>
    /// The key and IV are converted from UTF-8 strings to byte arrays. Ensure that your key and IV strings
    /// are exactly the required length when encoded as UTF-8 bytes. These values are stored and reused
    /// for all encryption and decryption operations performed by this instance.
    /// </remarks>
    public EncryptionService(string key, string iv)
    {
        if (key.Length != 32 || iv.Length != 16)
            throw new ArgumentException("Key must be 32 bytes and IV must be 16 bytes long.");

        this.key = Encoding.UTF8.GetBytes(key);
        this.iv = Encoding.UTF8.GetBytes(iv);
    }


    /// <summary>
    /// Encrypts the specified plain text string using AES encryption.
    /// </summary>
    /// <param name="plainText">The plain text string to encrypt.</param>
    /// <returns>A base64-encoded string containing the encrypted data.</returns>
    /// <remarks>
    /// This method uses the AES algorithm with the key and IV provided during construction.
    /// The encrypted output is encoded as a base64 string for easy storage and transmission.
    /// The same plain text will always produce the same encrypted output when using the same key and IV.
    /// </remarks>
    public string Encrypt(string plainText)
    {
        using (Aes aesAlg = Aes.Create())
        {
            aesAlg.Key = key;
            aesAlg.IV = iv;

            ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

            using (MemoryStream msEncrypt = new MemoryStream())
            {
                using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                {
                    swEncrypt.Write(plainText);
                }

                return Convert.ToBase64String(msEncrypt.ToArray());
            }
        }
    }

    /// <summary>
    /// Decrypts the specified cipher text string using AES decryption.
    /// </summary>
    /// <param name="cipherText">The base64-encoded encrypted string to decrypt.</param>
    /// <returns>The decrypted plain text string.</returns>
    /// <exception cref="FormatException">Thrown when the cipher text is not a valid base64 string.</exception>
    /// <exception cref="CryptographicException">
    /// Thrown when the cipher text cannot be decrypted, which may occur if it was encrypted with a different key or IV,
    /// or if the data has been corrupted.
    /// </exception>
    /// <remarks>
    /// This method uses the AES algorithm with the key and IV provided during construction.
    /// The cipher text must be a base64-encoded string that was previously encrypted using the <see cref="Encrypt"/> method
    /// with the same key and IV. If the key or IV differs, or if the cipher text has been modified, decryption will fail.
    /// </remarks>
    public string Decrypt(string cipherText)
    {
        using (Aes aesAlg = Aes.Create())
        {
            aesAlg.Key = key;
            aesAlg.IV = iv;

            ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

            using (MemoryStream msDecrypt = new MemoryStream(Convert.FromBase64String(cipherText)))
            using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
            using (StreamReader srDecrypt = new StreamReader(csDecrypt))
            {
                return srDecrypt.ReadToEnd();
            }
        }
    }
}