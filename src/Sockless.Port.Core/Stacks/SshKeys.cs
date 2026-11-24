using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sockless.Port.Stacks;

/// <summary>A host key from <c>known_hosts</c>, for people to compare with what the host publishes.</summary>
public sealed record SshHostKey(string Host, string Type, string Fingerprint);

/// <summary>
/// SSH keys for git: generating a deploy key, checking a pasted private key, and fingerprints of
/// the host keys a server presented.
/// </summary>
public static class SshKeys
{
    private const string Magic = "openssh-key-v1\0";
    private const string EcdsaType = "ecdsa-sha2-nistp256";
    private const string Curve = "nistp256";

    /// <summary>
    /// A new ECDSA P-256 key pair: the private key in OpenSSH's own format (unencrypted, which
    /// OpenSSH reads with or without OpenSSL), and the public key as an <c>authorized_keys</c> line.
    /// </summary>
    public static (string PrivateKey, string PublicKey) Generate(string comment)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);
        byte[] point = [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];

        var publicBlob = new SshWriter().String(EcdsaType).String(Curve).Bytes(point).ToArray();

        var check = RandomNumberGenerator.GetBytes(4);
        var secret = new SshWriter()
            .Raw(check).Raw(check)
            .String(EcdsaType).String(Curve).Bytes(point).MpInt(parameters.D!).String(comment);
        // Padded to the cipher's block size (8 for "none") with 1, 2, 3, …
        for (byte pad = 1; secret.Length % 8 != 0; pad++)
            secret.Raw([pad]);

        var file = new SshWriter()
            .Raw(Encoding.ASCII.GetBytes(Magic))
            .String("none").String("none").Bytes([])
            .UInt32(1)
            .Bytes(publicBlob)
            .Bytes(secret.ToArray())
            .ToArray();
        CryptographicOperations.ZeroMemory(parameters.D);

        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        var base64 = Convert.ToBase64String(file);
        for (var i = 0; i < base64.Length; i += 70)
            pem.Append(base64.AsSpan(i, Math.Min(70, base64.Length - i))).Append('\n');
        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return (pem.ToString(), $"{EcdsaType} {Convert.ToBase64String(publicBlob)} {comment}");
    }

    /// <summary>The key as ssh reads it: LF line endings, no stray spaces, a final newline.</summary>
    public static string Normalize(string key) =>
        string.Join('\n', key.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)) + "\n";

    /// <summary>Why a pasted private key can't be used, or null when it looks usable.</summary>
    public static string? Problem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Paste the private key, or generate one.";
        key = Normalize(key);
        if (key.Length > 16 * 1024)
            return "That's too long for a private key.";
        if (key.Contains("PUBLIC KEY", StringComparison.Ordinal) || key.StartsWith("ssh-", StringComparison.Ordinal) || key.StartsWith("ecdsa-", StringComparison.Ordinal))
            return "That's a public key. Paste the private key (it starts with -----BEGIN … PRIVATE KEY-----), and add the public one to the repository.";
        if (!key.StartsWith("-----BEGIN ", StringComparison.Ordinal) || !key.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            return "That isn't a private key. Paste it whole, from -----BEGIN … PRIVATE KEY----- to the END line.";
        if (key.Contains("ENCRYPTED", StringComparison.Ordinal) || IsEncryptedOpenSsh(key))
            return "The key is protected with a passphrase, which Sockless Port can't enter. Use a key without one, such as a deploy key made for this.";
        return null;
    }

    /// <summary>The host keys in <c>known_hosts</c> text, with their SHA-256 fingerprints as <c>ssh-keygen -l</c> prints them.</summary>
    public static IReadOnlyList<SshHostKey> HostKeys(string? knownHosts)
    {
        List<SshHostKey> keys = [];
        foreach (var line in (knownHosts ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#'))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0].StartsWith('@'))
                continue;
            if (Fingerprint(parts[2]) is { } fingerprint)
                keys.Add(new SshHostKey(parts[0], parts[1], fingerprint));
        }
        return keys;
    }

    /// <summary><c>SHA256:…</c> of a base64 key blob; null when it isn't base64.</summary>
    public static string? Fingerprint(string base64Blob)
    {
        try
        {
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(base64Blob))).TrimEnd('=');
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>OpenSSH's own format names its cipher right after the magic: "none" when unencrypted.</summary>
    private static bool IsEncryptedOpenSsh(string key)
    {
        if (!key.Contains("BEGIN OPENSSH PRIVATE KEY", StringComparison.Ordinal))
            return false;
        var body = string.Concat(key.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)));
        try
        {
            var bytes = Convert.FromBase64String(body);
            if (bytes.Length < Magic.Length + 4 || Encoding.ASCII.GetString(bytes, 0, Magic.Length) != Magic)
                return false;
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(Magic.Length));
            if (length < 0 || Magic.Length + 4 + length > bytes.Length)
                return false;
            return Encoding.ASCII.GetString(bytes, Magic.Length + 4, length) != "none";
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The SSH wire encoding (RFC 4251): big-endian lengths before strings.</summary>
    private sealed class SshWriter
    {
        private readonly MemoryStream _stream = new();

        public int Length => (int)_stream.Length;

        public SshWriter Raw(ReadOnlySpan<byte> bytes)
        {
            _stream.Write(bytes);
            return this;
        }

        public SshWriter UInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            return Raw(buffer);
        }

        public SshWriter Bytes(ReadOnlySpan<byte> bytes) => UInt32((uint)bytes.Length).Raw(bytes);

        public SshWriter String(string text) => Bytes(Encoding.UTF8.GetBytes(text));

        /// <summary>A positive big integer: no leading zeros, and a 0 first when the top bit is set.</summary>
        public SshWriter MpInt(ReadOnlySpan<byte> bigEndian)
        {
            var start = 0;
            while (start < bigEndian.Length && bigEndian[start] == 0)
                start++;
            var value = bigEndian[start..];
            return value.Length > 0 && (value[0] & 0x80) != 0 ? Bytes([0, .. value]) : Bytes(value);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
