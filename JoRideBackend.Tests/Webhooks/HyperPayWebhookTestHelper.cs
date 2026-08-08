using System.Security.Cryptography;
using System.Text;

namespace JoRideBackend.Tests.Webhooks;

/// <summary>
/// Encrypts test payloads the same way real OPPWA webhooks are encrypted (AES-256-GCM,
/// 96-bit nonce, 128-bit tag — see HyperPayWebhookController's mechanism doc), so tests
/// exercise HyperPayWebhookService's actual decrypt path rather than a mocked-out one.
/// </summary>
public static class HyperPayWebhookTestHelper
{
    public static string RandomSecretHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); // 64 hex chars

    public static (string BodyHex, string IvHex, string TagHex) Encrypt(string plaintextJson, string secretHex)
    {
        var key = Convert.FromHexString(secretHex);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(plaintextJson);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aesGcm = new AesGcm(key, tag.Length);
        aesGcm.Encrypt(iv, plaintext, ciphertext, tag);

        return (Convert.ToHexString(ciphertext), Convert.ToHexString(iv), Convert.ToHexString(tag));
    }
}
