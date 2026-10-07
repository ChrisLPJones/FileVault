using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Backend.Services;

// Encrypts stored files with AES-256-GCM.
//
// Each file gets its own random data key. The data key is stored in the database
// "wrapped" (encrypted with the master key from configuration and bound to the
// file's GUID), so the files on disk are useless without both the database and
// the master key.
//
// On-disk format:  "FVE1" | chunk size (int32 LE) | chunk 0 | chunk 1 | ...
// Each chunk is the ciphertext of up to `chunk size` plaintext bytes followed by
// a 16-byte tag. The nonce is the chunk index plus a "final chunk" flag, so a
// file that has been truncated, extended or had chunks reordered won't decrypt.
// Chunks can be decrypted independently, which keeps memory use flat and lets
// downloads support range requests.
public class FileEncryption
{
    public const int ChunkSize = 64 * 1024;
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private const int KeySize = 32;
    private const int HeaderSize = 8;
    private static readonly byte[] Magic = "FVE1"u8.ToArray();

    private readonly byte[] _masterKey;

    public FileEncryption(IConfiguration config)
    {
        _masterKey = ParseMasterKey(config["Encryption:MasterKey"])
            ?? throw new InvalidOperationException("Encryption:MasterKey must be a base64-encoded 32-byte key.");
    }

    // Returns the key bytes, or null if the value isn't a base64-encoded 32-byte key
    public static byte[] ParseMasterKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var key = Convert.FromBase64String(value);
            return key.Length == KeySize ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }



    // Create a random data key for a new file, plus its wrapped form for the database
    public (byte[] dataKey, string wrappedKey) CreateDataKey(string fileGuid)
    {
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);
        return (dataKey, WrapKey(dataKey, fileGuid));
    }

    // Encrypt a data key with the master key, bound to the file GUID
    public string WrapKey(byte[] dataKey, string fileGuid)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[KeySize];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Encrypt(nonce, dataKey, cipher, tag, Encoding.UTF8.GetBytes(fileGuid));

        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    // Decrypt a wrapped data key; throws if it was tampered with or belongs to another file
    public byte[] UnwrapKey(string wrappedKey, string fileGuid)
    {
        var bytes = Convert.FromBase64String(wrappedKey);
        if (bytes.Length != NonceSize + KeySize + TagSize)
            throw new CryptographicException("Invalid wrapped key.");

        var dataKey = new byte[KeySize];
        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Decrypt(
            bytes.AsSpan(0, NonceSize),
            bytes.AsSpan(NonceSize, KeySize),
            bytes.AsSpan(NonceSize + KeySize, TagSize),
            dataKey,
            Encoding.UTF8.GetBytes(fileGuid));

        return dataKey;
    }



    // Encrypt everything from input into output; returns the plaintext length
    public static async Task<long> EncryptAsync(Stream input, Stream output, byte[] dataKey, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), ChunkSize);
        await output.WriteAsync(header, ct);

        using var aes = new AesGcm(dataKey, TagSize);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var cipher = new byte[ChunkSize + TagSize];
        var nonce = new byte[NonceSize];

        var currentLength = await ReadFullAsync(input, current, ct);
        long index = 0;
        long total = 0;

        while (true)
        {
            // Read ahead so the last chunk can be flagged as final
            var nextLength = currentLength == ChunkSize ? await ReadFullAsync(input, next, ct) : 0;
            var final = nextLength == 0;

            ChunkNonce(nonce, index, final);
            aes.Encrypt(nonce, current.AsSpan(0, currentLength), cipher.AsSpan(0, currentLength), cipher.AsSpan(currentLength, TagSize));
            await output.WriteAsync(cipher.AsMemory(0, currentLength + TagSize), ct);
            total += currentLength;

            if (final)
                return total;

            (current, next) = (next, current);
            currentLength = nextLength;
            index++;
        }
    }

    // Open a stored file for reading as plaintext (seekable, so range requests work)
    public static Stream OpenDecryptedRead(string path, byte[] dataKey, long plaintextLength)
    {
        var inner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        try
        {
            return new DecryptingStream(inner, dataKey, plaintextLength);
        }
        catch
        {
            inner.Dispose();
            throw;
        }
    }

    private static void ChunkNonce(Span<byte> nonce, long index, bool final)
    {
        nonce.Clear();
        nonce[0] = final ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], index);
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }



    // Read-only, seekable plaintext view over an encrypted file
    private sealed class DecryptingStream : Stream
    {
        private readonly FileStream _inner;
        private readonly AesGcm _aes;
        private readonly long _length;
        private readonly int _chunkSize;
        private readonly long _chunkCount;
        private readonly byte[] _cipher;
        private readonly byte[] _plain;
        private readonly byte[] _nonce = new byte[NonceSize];
        private long _position;
        private long _loadedChunk = -1;
        private int _loadedLength;

        public DecryptingStream(FileStream inner, byte[] dataKey, long length)
        {
            _inner = inner;
            _length = length;

            var header = new byte[HeaderSize];
            inner.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual(Magic))
                throw new CryptographicException("Stored file is not in the encrypted format.");

            _chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (_chunkSize <= 0 || _chunkSize > 16 * 1024 * 1024)
                throw new CryptographicException("Invalid chunk size.");

            _chunkCount = length == 0 ? 1 : (length + _chunkSize - 1) / _chunkSize;
            if (inner.Length != HeaderSize + length + _chunkCount * TagSize)
                throw new CryptographicException("Stored file length doesn't match its metadata.");

            _cipher = new byte[_chunkSize + TagSize];
            _plain = new byte[_chunkSize];
            _aes = new AesGcm(dataKey, TagSize);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length || buffer.Length == 0)
                return 0;

            var index = _position / _chunkSize;
            if (index != _loadedChunk)
            {
                _inner.Position = ChunkOffset(index);
                _inner.ReadExactly(_cipher, 0, ChunkCipherLength(index));
                DecryptChunk(index);
            }

            return CopyFromChunk(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= _length || buffer.Length == 0)
                return ValueTask.FromResult(0);

            var index = _position / _chunkSize;
            return index == _loadedChunk
                ? ValueTask.FromResult(CopyFromChunk(buffer.Span))
                : LoadAndReadAsync(index, buffer, ct);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private async ValueTask<int> LoadAndReadAsync(long index, Memory<byte> buffer, CancellationToken ct)
        {
            _inner.Position = ChunkOffset(index);
            await _inner.ReadExactlyAsync(_cipher.AsMemory(0, ChunkCipherLength(index)), ct);
            DecryptChunk(index);
            return CopyFromChunk(buffer.Span);
        }

        private long ChunkOffset(long index) => HeaderSize + index * (_chunkSize + TagSize);

        private int ChunkPlainLength(long index) =>
            index == _chunkCount - 1 ? (int)(_length - index * _chunkSize) : _chunkSize;

        private int ChunkCipherLength(long index) => ChunkPlainLength(index) + TagSize;

        private void DecryptChunk(long index)
        {
            var plainLength = ChunkPlainLength(index);
            ChunkNonce(_nonce, index, index == _chunkCount - 1);

            // Throws AuthenticationTagMismatchException if the chunk was tampered with
            _aes.Decrypt(_nonce, _cipher.AsSpan(0, plainLength), _cipher.AsSpan(plainLength, TagSize), _plain.AsSpan(0, plainLength));

            _loadedChunk = index;
            _loadedLength = plainLength;
        }

        private int CopyFromChunk(Span<byte> buffer)
        {
            var offsetInChunk = (int)(_position - _loadedChunk * _chunkSize);
            var count = Math.Min(buffer.Length, _loadedLength - offsetInChunk);
            _plain.AsSpan(offsetInChunk, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(_plain);
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
            await base.DisposeAsync();
        }
    }
}
