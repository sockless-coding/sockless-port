using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sockless.Port.Backup;

/// <summary>
/// Encryption of backups with a passphrase. The format: a header (<c>SPBKENC1</c>, the PBKDF2
/// iteration count as a big-endian uint32, a 16-byte salt), then records of at most 64 KiB:
/// a big-endian uint32 length whose top bit marks the last record, the AES-256-GCM ciphertext and
/// its 16-byte tag. The key is PBKDF2-SHA256 of the passphrase; each record's nonce is its
/// number and the last-record flag, and the header is the associated data, so records can't be
/// reordered, dropped, cut short or moved to another file.
/// </summary>
public static class BackupCipher
{
    public const string Extension = ".enc";
    public const int MinPassphraseLength = 12;
    /// <summary>Short enough that the stored, encrypted passphrase fits its setting.</summary>
    public const int MaxPassphraseLength = 128;

    internal const int ChunkSize = 64 * 1024;
    internal const int TagSize = 16;
    internal const int SaltSize = 16;
    internal const int HeaderSize = 8 + 4 + SaltSize;
    internal const uint FinalFlag = 0x8000_0000;

    /// <summary>OWASP's figure for PBKDF2-SHA256 (2023).</summary>
    internal const int DefaultIterations = 600_000;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;

    private static ReadOnlySpan<byte> Magic => "SPBKENC1"u8;

    public static string? PassphraseProblem(string passphrase) => passphrase.Length switch
    {
        < MinPassphraseLength => $"Use a passphrase of at least {MinPassphraseLength} characters.",
        > MaxPassphraseLength => $"Use a passphrase of at most {MaxPassphraseLength} characters.",
        _ => null,
    };

    /// <summary>A stream that encrypts what's written to it into <paramref name="output"/>. Dispose it to write the last record.</summary>
    public static Stream Encrypt(Stream output, string passphrase, bool leaveOpen = false, int iterations = DefaultIterations)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)iterations);
        RandomNumberGenerator.Fill(header.AsSpan(12, SaltSize));
        return new EncryptingStream(output, DeriveKey(passphrase, header), header, leaveOpen);
    }

    /// <summary>
    /// Opens a backup for reading: decrypted when it's encrypted, as it is otherwise.
    /// </summary>
    /// <exception cref="BackupRejectedException">It's encrypted and the passphrase is missing, or its header isn't valid.</exception>
    public static async Task<Stream> OpenReadAsync(Stream input, string? passphrase, CancellationToken cancellationToken = default)
    {
        var start = new byte[Magic.Length];
        var read = await input.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false, cancellationToken);
        if (read < start.Length || !start.AsSpan().SequenceEqual(Magic))
            return new PrefixedStream(start.AsMemory(0, read), input);

        if (string.IsNullOrEmpty(passphrase))
            throw new BackupRejectedException("This backup is encrypted. Enter its passphrase.");
        var header = new byte[HeaderSize];
        start.CopyTo(header, 0);
        if (await input.ReadAtLeastAsync(header.AsMemory(start.Length), HeaderSize - start.Length, throwOnEndOfStream: false, cancellationToken) < HeaderSize - start.Length)
            throw new BackupRejectedException("The encrypted backup is cut short.");
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        if (iterations is < MinIterations or > MaxIterations)
            throw new BackupRejectedException("The encrypted backup's header isn't valid.");
        return new DecryptingStream(input, DeriveKey(passphrase, header), header);
    }

    private static byte[] DeriveKey(string passphrase, byte[] header) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), header.AsSpan(12, SaltSize),
            (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)), HashAlgorithmName.SHA256, 32);

    private static void Nonce(Span<byte> nonce, ulong counter, bool final)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce, counter);
        nonce[11] = final ? (byte)1 : (byte)0;
    }

    private sealed class EncryptingStream(Stream output, byte[] key, byte[] header, bool leaveOpen) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _plain = new byte[ChunkSize];
        private readonly byte[] _record = new byte[4 + ChunkSize + TagSize];
        private int _buffered;
        private ulong _counter;
        private bool _headerWritten;
        private bool _closed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_closed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            while (buffer.Length > 0)
            {
                // A full buffer is written only once more data arrives, so the last record is always the one written at the end.
                if (_buffered == ChunkSize)
                    output.Write(Seal(final: false).Span);
                var take = Math.Min(buffer.Length, ChunkSize - _buffered);
                buffer[..take].CopyTo(_plain.AsSpan(_buffered));
                _buffered += take;
                buffer = buffer[take..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            while (buffer.Length > 0)
            {
                if (_buffered == ChunkSize)
                    await output.WriteAsync(Seal(final: false), cancellationToken);
                var take = Math.Min(buffer.Length, ChunkSize - _buffered);
                buffer.Span[..take].CopyTo(_plain.AsSpan(_buffered));
                _buffered += take;
                buffer = buffer[take..];
            }
        }

        /// <summary>Encrypts the buffered data into a record, with the header in front of the first.</summary>
        private ReadOnlyMemory<byte> Seal(bool final)
        {
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _counter++, final);
            BinaryPrimitives.WriteUInt32BigEndian(_record, (uint)_buffered | (final ? FinalFlag : 0));
            _aes.Encrypt(nonce, _plain.AsSpan(0, _buffered), _record.AsSpan(4, _buffered), _record.AsSpan(4 + _buffered, TagSize), header);
            var length = 4 + _buffered + TagSize;
            _buffered = 0;
            if (_headerWritten)
                return _record.AsMemory(0, length);
            _headerWritten = true;
            var first = new byte[HeaderSize + length];
            header.CopyTo(first, 0);
            _record.AsSpan(0, length).CopyTo(first.AsSpan(HeaderSize));
            return first;
        }

        public override void Flush() => output.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                output.Write(Seal(final: true).Span);
                output.Flush();
                _aes.Dispose();
                if (!leaveOpen)
                    output.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                await output.WriteAsync(Seal(final: true));
                await output.FlushAsync();
                _aes.Dispose();
                if (!leaveOpen)
                    await output.DisposeAsync();
            }
            GC.SuppressFinalize(this);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DecryptingStream(Stream input, byte[] key, byte[] header) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _plain = new byte[ChunkSize];
        private readonly byte[] _cipher = new byte[ChunkSize + TagSize];
        private int _offset;
        private int _available;
        private ulong _counter;
        private bool _final;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var array = new byte[buffer.Length];
            var read = ReadCoreAsync(array, useAsync: false, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            array.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadCoreAsync(buffer.AsMemory(offset, count), useAsync: true, cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ReadCoreAsync(buffer, useAsync: true, cancellationToken);

        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, bool useAsync, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0)
                return 0;
            while (_available == 0)
            {
                if (_final)
                {
                    // Nothing may follow the last record.
                    var extra = new byte[1];
                    if (await ReadAsync(extra, useAsync, cancellationToken) != 0)
                        throw new BackupRejectedException("The encrypted backup has data after its end, so it was changed.");
                    return 0;
                }
                await OpenRecordAsync(useAsync, cancellationToken);
            }
            var take = Math.Min(buffer.Length, _available);
            _plain.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;
            _available -= take;
            return take;
        }

        private async ValueTask OpenRecordAsync(bool useAsync, CancellationToken cancellationToken)
        {
            var prefix = new byte[4];
            if (await ReadAsync(prefix, useAsync, cancellationToken) < 4)
                throw new BackupRejectedException("The encrypted backup is cut short.");
            var value = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            var final = (value & FinalFlag) != 0;
            var length = (int)(value & ~FinalFlag);
            if (length > ChunkSize)
                throw new BackupRejectedException("The encrypted backup is damaged.");
            if (await ReadAsync(_cipher.AsMemory(0, length + TagSize), useAsync, cancellationToken) < length + TagSize)
                throw new BackupRejectedException("The encrypted backup is cut short.");

            var nonce = new byte[12];
            Nonce(nonce, _counter++, final);
            try
            {
                _aes.Decrypt(nonce, _cipher.AsSpan(0, length), _cipher.AsSpan(length, TagSize), _plain.AsSpan(0, length), header);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new BackupRejectedException(_counter == 1
                    ? "The passphrase is wrong, or the backup is damaged."
                    : "The encrypted backup is damaged.");
            }
            _offset = 0;
            _available = length;
            _final = final;
        }

        /// <summary>Reads until <paramref name="buffer"/> is full or the input ends.</summary>
        private async ValueTask<int> ReadAsync(Memory<byte> buffer, bool useAsync, CancellationToken cancellationToken) =>
            useAsync
                ? await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                : input.ReadAtLeast(buffer.Span, buffer.Length, throwOnEndOfStream: false);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _aes.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The bytes already read to look for the header, then the rest of the input.</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream input) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefix.Length == 0)
                return input.Read(buffer);
            var take = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..take].CopyTo(buffer);
            _prefix = _prefix[take..];
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.Length == 0)
                return input.ReadAsync(buffer, cancellationToken);
            var take = Math.Min(buffer.Length, _prefix.Length);
            _prefix[..take].CopyTo(buffer);
            _prefix = _prefix[take..];
            return ValueTask.FromResult(take);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
