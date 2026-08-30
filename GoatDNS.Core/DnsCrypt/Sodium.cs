using System.Runtime.InteropServices;

namespace GoatDNS.Core.DnsCrypt;

/// <summary>Minimal libsodium interop: exactly the primitives the DNSCrypt v2 protocol needs.</summary>
internal static class Sodium
{
    private const string Lib = "libsodium";

    public const int PublicKeyBytes = 32;
    public const int SecretKeyBytes = 32;
    public const int SharedKeyBytes = 32;
    public const int NonceBytes = 24;
    public const int MacBytes = 16;
    public const int SignatureBytes = 64;

    static Sodium()
    {
        if (sodium_init() < 0) throw new InvalidOperationException("libsodium failed to initialize");
    }

    [DllImport(Lib)] private static extern int sodium_init();
    [DllImport(Lib)] private static extern int crypto_box_keypair(byte[] pk, byte[] sk);
    [DllImport(Lib)] private static extern int crypto_box_beforenm(byte[] k, byte[] pk, byte[] sk);
    [DllImport(Lib)] private static extern int crypto_box_curve25519xchacha20poly1305_beforenm(byte[] k, byte[] pk, byte[] sk);
    [DllImport(Lib)] private static extern int crypto_secretbox_easy(byte[] c, byte[] m, ulong mlen, byte[] n, byte[] k);
    [DllImport(Lib)] private static extern int crypto_secretbox_open_easy(byte[] m, byte[] c, ulong clen, byte[] n, byte[] k);
    [DllImport(Lib)] private static extern int crypto_secretbox_xchacha20poly1305_easy(byte[] c, byte[] m, ulong mlen, byte[] n, byte[] k);
    [DllImport(Lib)] private static extern int crypto_secretbox_xchacha20poly1305_open_easy(byte[] m, byte[] c, ulong clen, byte[] n, byte[] k);
    [DllImport(Lib)] private static extern int crypto_sign_ed25519_verify_detached(byte[] sig, byte[] m, ulong mlen, byte[] pk);
    [DllImport(Lib)] private static extern void randombytes_buf(byte[] buf, nuint size);

    public static (byte[] PublicKey, byte[] SecretKey) BoxKeypair()
    {
        var pk = new byte[PublicKeyBytes];
        var sk = new byte[SecretKeyBytes];
        crypto_box_keypair(pk, sk);
        return (pk, sk);
    }

    /// <summary>X25519 + HSalsa20 (es-version 1) or X25519 + HChaCha20 (es-version 2) shared key.</summary>
    public static byte[] SharedKey(bool xchacha, byte[] resolverPk, byte[] clientSk)
    {
        var k = new byte[SharedKeyBytes];
        int rc = xchacha
            ? crypto_box_curve25519xchacha20poly1305_beforenm(k, resolverPk, clientSk)
            : crypto_box_beforenm(k, resolverPk, clientSk);
        if (rc != 0) throw new InvalidOperationException("Key exchange failed");
        return k;
    }

    public static byte[] SecretboxSeal(bool xchacha, byte[] message, byte[] nonce, byte[] key)
    {
        var c = new byte[message.Length + MacBytes];
        int rc = xchacha
            ? crypto_secretbox_xchacha20poly1305_easy(c, message, (ulong)message.Length, nonce, key)
            : crypto_secretbox_easy(c, message, (ulong)message.Length, nonce, key);
        if (rc != 0) throw new InvalidOperationException("Encryption failed");
        return c;
    }

    public static byte[] SecretboxOpen(bool xchacha, byte[] ciphertext, byte[] nonce, byte[] key)
    {
        if (ciphertext.Length < MacBytes) throw new System.Security.Cryptography.CryptographicException("Ciphertext too short");
        var m = new byte[ciphertext.Length - MacBytes];
        int rc = xchacha
            ? crypto_secretbox_xchacha20poly1305_open_easy(m, ciphertext, (ulong)ciphertext.Length, nonce, key)
            : crypto_secretbox_open_easy(m, ciphertext, (ulong)ciphertext.Length, nonce, key);
        if (rc != 0) throw new System.Security.Cryptography.CryptographicException("Decryption/authentication failed");
        return m;
    }

    public static bool Ed25519Verify(byte[] signature, byte[] message, byte[] publicKey) =>
        crypto_sign_ed25519_verify_detached(signature, message, (ulong)message.Length, publicKey) == 0;

    public static byte[] RandomBytes(int count)
    {
        var buf = new byte[count];
        randombytes_buf(buf, (nuint)count);
        return buf;
    }
}
