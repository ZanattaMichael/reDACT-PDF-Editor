using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace PdfEditor.Core.Pdf;

/// <summary>
/// The primitives the standard security handler is built from, all BouncyCastle: MD5 and RC4 for
/// the legacy revisions, SHA-2 and AES for the current ones, and the random bytes that keys,
/// salts, IVs and document IDs are made of.
/// </summary>
internal static class PdfCrypto
{
    private static readonly SecureRandom Random = new();

    public static byte[] RandomBytes(int count)
    {
        lock (Random) return SecureRandom.GetNextBytes(Random, count);
    }

    public static byte[] Md5(byte[] data) => Hash(new MD5Digest(), data);

    public static byte[] Md5(byte[] data, int length) => Hash(new MD5Digest(), data, length);

    public static byte[] Sha256(byte[] data) => Hash(new Sha256Digest(), data);

    public static byte[] Sha384(byte[] data) => Hash(new Sha384Digest(), data);

    public static byte[] Sha512(byte[] data) => Hash(new Sha512Digest(), data);

    private static byte[] Hash(IDigest digest, byte[] data, int? length = null)
    {
        digest.BlockUpdate(data, 0, length ?? data.Length);
        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return hash;
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var rc4 = new RC4Engine();
        rc4.Init(true, new KeyParameter(key));
        var output = new byte[data.Length];
        rc4.ProcessBytes(data, 0, data.Length, output, 0);
        return output;
    }

    /// <summary>AES-CBC encryption; with <paramref name="pad"/> the data is PKCS#7-padded, otherwise it must fill whole blocks.</summary>
    public static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] data, bool pad) =>
        AesCbc(encrypt: true, pad, new ParametersWithIV(new KeyParameter(key), iv), data, 0, data.Length);

    /// <summary>AES-CBC decryption of whole blocks, leaving any padding for the caller to judge.</summary>
    public static byte[] AesCbcDecrypt(byte[] key, byte[] iv, int ivOffset, byte[] data, int offset, int length) =>
        AesCbc(encrypt: false, pad: false, new ParametersWithIV(new KeyParameter(key), iv, ivOffset, 16), data, offset, length);

    private static byte[] AesCbc(bool encrypt, bool pad, ParametersWithIV keyAndIv, byte[] data, int offset, int length)
    {
        var cbc = new CbcBlockCipher(AesUtilities.CreateEngine());
        var cipher = pad ? new PaddedBufferedBlockCipher(cbc, new Pkcs7Padding()) : new BufferedBlockCipher(cbc);
        cipher.Init(encrypt, keyAndIv);
        return cipher.DoFinal(data, offset, length);
    }

    /// <summary>AES-ECB encryption of a single 16-byte block.</summary>
    public static byte[] AesEcbEncryptBlock(byte[] key, byte[] block)
    {
        var aes = AesUtilities.CreateEngine();
        aes.Init(true, new KeyParameter(key));
        var output = new byte[16];
        aes.ProcessBlock(block, 0, output, 0);
        return output;
    }
}
