// CONSUMER-SUPPLIED HELPER — RSA encrypt/decrypt.
// Uses System.Security.Cryptography, which the sandbox allows (it is on the
// AllowedAssemblies reference list and is NOT a banned namespace).
// The key material is supplied by the HOST (created in Program.cs and passed in
// via the script services / config) — the helper never generates or stores keys.
// In the real runtime the script would read these from the secret store via
// ScriptBase.GetSecret(...).
using System;
using System.Security.Cryptography;
using System.Text;

namespace Acme.Helpers;

public static class RsaCryptoHelper
{
    private static readonly RSAEncryptionPadding Padding = RSAEncryptionPadding.OaepSHA256;

    /// <summary>Encrypts UTF-8 text with a Base64 SubjectPublicKeyInfo (SPKI) public key.</summary>
    public static string Encrypt(string plainText, string publicKeyBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainText);
        ArgumentException.ThrowIfNullOrEmpty(publicKeyBase64);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        var cipher = rsa.Encrypt(Encoding.UTF8.GetBytes(plainText), Padding);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>Decrypts Base64 ciphertext with a Base64 PKCS#8 private key.</summary>
    public static string Decrypt(string cipherBase64, string privateKeyBase64)
    {
        ArgumentException.ThrowIfNullOrEmpty(cipherBase64);
        ArgumentException.ThrowIfNullOrEmpty(privateKeyBase64);

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        var plain = rsa.Decrypt(Convert.FromBase64String(cipherBase64), Padding);
        return Encoding.UTF8.GetString(plain);
    }
}
