using System.Security.Cryptography;
using System.Text;
using BigMission.Shared.Utilities;

namespace BigMission.Shared.Tests.Utilities;

/// <summary>
/// <see cref="EncryptionService"/> is AES-CBC with a caller-supplied key and fixed IV, used to keep
/// stored credentials out of plain text. These tests pin the round trip, the guard rails on the key
/// and IV, and what happens when the wrong key meets a ciphertext - the case that decides whether a
/// caller sees a clean failure or silent garbage.
/// </summary>
[TestClass]
public class EncryptionServiceTests
{
    private const string Key = "01234567890123456789012345678901";
    private const string Iv = "0123456789012345";

    private static EncryptionService Create(string key = Key, string iv = Iv) => new(key, iv);

    #region Construction

    [TestMethod]
    [DataRow("", DisplayName = "empty key")]
    [DataRow("too-short", DisplayName = "key under 32")]
    [DataRow("012345678901234567890123456789012", DisplayName = "key over 32")]
    public void Constructor_KeyIsNot32Bytes_Throws(string key)
    {
        Assert.ThrowsExactly<ArgumentException>(() => Create(key: key));
    }

    [TestMethod]
    [DataRow("", DisplayName = "empty IV")]
    [DataRow("short", DisplayName = "IV under 16")]
    [DataRow("01234567890123456", DisplayName = "IV over 16")]
    public void Constructor_IvIsNot16Bytes_Throws(string iv)
    {
        Assert.ThrowsExactly<ArgumentException>(() => Create(iv: iv));
    }

    [TestMethod]
    public void Constructor_MultiByteKeyOfTheRightCharacterCount_IsRejected()
    {
        // The guard measures UTF-8 bytes, which is what AES sizes by. Counting characters instead let
        // a 32-character key with one accented letter - 33 bytes - through, and the failure surfaced
        // as a CryptographicException on first use rather than here, where the key is still in view.
        var key = new string('a', 31) + "é";
        Assert.AreEqual(32, key.Length);
        Assert.AreEqual(33, Encoding.UTF8.GetByteCount(key));

        var ex = Assert.ThrowsExactly<ArgumentException>(() => Create(key: key));

        StringAssert.Contains(ex.Message, "the key is 33 bytes");
    }

    [TestMethod]
    public void Constructor_MultiByteIvOfTheRightCharacterCount_IsRejected()
    {
        var iv = new string('a', 15) + "é";
        Assert.AreEqual(16, iv.Length);
        Assert.AreEqual(17, Encoding.UTF8.GetByteCount(iv));

        var ex = Assert.ThrowsExactly<ArgumentException>(() => Create(iv: iv));

        StringAssert.Contains(ex.Message, "the IV is 17 bytes");
    }

    /// <summary>
    /// A 32-byte key ending in an unpaired surrogate.
    /// </summary>
    /// <remarks>
    /// Built at runtime rather than written as a literal or passed through a <c>DataRow</c>: an
    /// unpaired surrogate does not survive the round trip through attribute metadata, and arrives as
    /// two characters, which quietly changes the byte count the test depends on.
    /// </remarks>
    private static string KeyEndingInSurrogate(int codeUnit) => new string('a', 29) + (char)codeUnit;

    [TestMethod]
    [DataRow(0xD800, DisplayName = "unpaired high surrogate")]
    [DataRow(0xDC00, DisplayName = "unpaired low surrogate")]
    public void Constructor_KeyContainingAnUnpairedSurrogate_IsRejected(int codeUnit)
    {
        // Without a strict encoder these slip through: the default UTF-8 encoder substitutes the
        // three-byte replacement character, so both of these become 32-byte keys that pass the length
        // check - and the same 32 bytes, meaning two different key strings share one AES key and each
        // silently decrypts the other's data.
        var key = KeyEndingInSurrogate(codeUnit);
        Assert.AreEqual(32, Encoding.UTF8.GetByteCount(key), "This is exactly the case the length check alone would accept.");

        Assert.ThrowsExactly<EncoderFallbackException>(() => Create(key: key));
    }

    [TestMethod]
    public void Constructor_TwoDifferentSurrogateKeys_DoNotCollapseOntoOneAesKey()
    {
        // The hazard stated as the property that matters: two distinct key strings must never produce
        // the same key material. Both are rejected, so neither can decrypt the other's data.
        var high = KeyEndingInSurrogate(0xD800);
        var low = KeyEndingInSurrogate(0xDC00);

        Assert.AreNotEqual(high, low);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(high), Encoding.UTF8.GetByteCount(low), "Both encode to the same length under the replacing encoder.");
        Assert.ThrowsExactly<EncoderFallbackException>(() => Create(key: high));
        Assert.ThrowsExactly<EncoderFallbackException>(() => Create(key: low));
    }

    [TestMethod]
    public void Constructor_UnpairedSurrogateFailure_IsStillAnArgumentException()
    {
        // The documented contract is ArgumentException, and EncoderFallbackException derives from it,
        // so a caller catching the documented type still catches this.
        var ex = Assert.ThrowsExactly<EncoderFallbackException>(() => Create(key: KeyEndingInSurrogate(0xD800)));

        Assert.IsInstanceOfType<ArgumentException>(ex);
    }

    [TestMethod]
    public void Constructor_NullKeyOrIv_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Create(key: null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => Create(iv: null!));
    }

    [TestMethod]
    public void Constructor_MultiByteKeyOfTheRightByteCount_IsAccepted()
    {
        // The other side of the same rule: a key that is 32 bytes but fewer than 32 characters is
        // perfectly good AES key material and must not be turned away.
        var key = new string('a', 30) + "é";
        Assert.AreEqual(32, Encoding.UTF8.GetByteCount(key));

        var service = Create(key: key);

        Assert.AreEqual("round trip", service.Decrypt(service.Encrypt("round trip")));
    }

    #endregion

    #region Round trip

    [TestMethod]
    [DataRow("hello world", DisplayName = "ascii")]
    [DataRow("", DisplayName = "empty string")]
    [DataRow("0123456789abcdef", DisplayName = "exactly one block")]
    [DataRow("Zürich éè 你好 \U0001F3C1", DisplayName = "non-ascii and surrogate pairs")]
    public void EncryptThenDecrypt_ReturnsTheOriginal(string plainText)
    {
        var service = Create();

        Assert.AreEqual(plainText, service.Decrypt(service.Encrypt(plainText)));
    }

    [TestMethod]
    public void EncryptThenDecrypt_LongText_ReturnsTheOriginal()
    {
        var service = Create();
        var plainText = string.Concat(Enumerable.Repeat("The quick brown fox. ", 500));

        Assert.AreEqual(plainText, service.Decrypt(service.Encrypt(plainText)));
    }

    [TestMethod]
    public void Encrypt_DoesNotLeaveThePlainTextVisible()
    {
        var service = Create();

        var cipherText = service.Encrypt("secret-connection-string");

        Assert.IsFalse(cipherText.Contains("secret"));
        Assert.IsFalse(Encoding.UTF8.GetString(Convert.FromBase64String(cipherText)).Contains("secret"));
    }

    [TestMethod]
    public void Encrypt_SameInputTwice_ProducesTheSameCipherText()
    {
        // A fixed IV makes this deterministic, which is what lets callers compare stored values - and
        // is also why the same plaintext is recognizable across records. Pinned as intended behavior.
        var service = Create();

        Assert.AreEqual(service.Encrypt("repeatable"), service.Encrypt("repeatable"));
    }

    [TestMethod]
    public void Decrypt_CipherTextFromAnotherInstanceWithTheSameKeyAndIv_Succeeds()
    {
        // The type is documented as reusable across instances, which is what makes it safe to encrypt
        // in one process and decrypt in another.
        var cipherText = Create().Encrypt("shared");

        Assert.AreEqual("shared", Create().Decrypt(cipherText));
    }

    #endregion

    #region Failure

    [TestMethod]
    public void Decrypt_WrongKey_Throws()
    {
        var cipherText = Create().Encrypt("secret");
        var other = Create(key: "abcdefghijklmnopqrstuvwxyz012345");

        Assert.ThrowsExactly<CryptographicException>(() => other.Decrypt(cipherText));
    }

    [TestMethod]
    public void Decrypt_WrongIv_CorruptsOnlyTheFirstBlock()
    {
        // CBC recovers after one block, so a mismatched IV does not throw - it returns a value whose
        // tail is correct. Anything relying on decryption to detect a wrong IV would not.
        var plainText = "0123456789abcdef-this-tail-survives";
        var cipherText = Create().Encrypt(plainText);
        var other = Create(iv: "abcdefghijklmnop");

        var decrypted = other.Decrypt(cipherText);

        Assert.AreNotEqual(plainText, decrypted);
        StringAssert.EndsWith(decrypted, "-this-tail-survives");
    }

    [TestMethod]
    public void Decrypt_NotBase64_Throws()
    {
        Assert.ThrowsExactly<FormatException>(() => Create().Decrypt("not base64!!"));
    }

    [TestMethod]
    public void Decrypt_TamperedCipherText_Throws()
    {
        var bytes = Convert.FromBase64String(Create().Encrypt("secret"));
        bytes[^1] ^= 0xFF;

        Assert.ThrowsExactly<CryptographicException>(() => Create().Decrypt(Convert.ToBase64String(bytes)));
    }

    #endregion
}
