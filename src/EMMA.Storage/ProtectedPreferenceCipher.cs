using System.Security.Cryptography;
using System.Text;

namespace EMMA.Storage;

public sealed class ProtectedPreferenceCipher
{
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const byte PayloadVersion = 1;

    private readonly string _keyPath;
    private readonly Lock _keyLock = new();
    private byte[]? _cachedKey;

    public ProtectedPreferenceCipher(StorageOptions options)
    {
        var databaseDirectory = Path.GetDirectoryName(options.DatabasePath);
        if (string.IsNullOrWhiteSpace(databaseDirectory))
        {
            databaseDirectory = Path.Combine(Path.GetTempPath(), "EMMA");
        }

        Directory.CreateDirectory(databaseDirectory);
        _keyPath = Path.Combine(databaseDirectory, "plugin-preferences.key");
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var key = GetOrCreateKey();
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var payload = new byte[1 + NonceSizeBytes + TagSizeBytes + cipherBytes.Length];
        payload[0] = PayloadVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, payload, 1 + NonceSizeBytes + TagSizeBytes, cipherBytes.Length);
        return Convert.ToBase64String(payload);
    }

    public string? TryDecrypt(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var encoded = Convert.FromBase64String(payload);
            if (encoded.Length < 1 + NonceSizeBytes + TagSizeBytes || encoded[0] != PayloadVersion)
            {
                return null;
            }

            var key = GetOrCreateKey();
            var nonce = encoded.AsSpan(1, NonceSizeBytes).ToArray();
            var tag = encoded.AsSpan(1 + NonceSizeBytes, TagSizeBytes).ToArray();
            var cipherBytes = encoded.AsSpan(1 + NonceSizeBytes + TagSizeBytes).ToArray();
            var plainBytes = new byte[cipherBytes.Length];

            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    private byte[] GetOrCreateKey()
    {
        lock (_keyLock)
        {
            if (_cachedKey is not null)
            {
                return _cachedKey;
            }

            if (File.Exists(_keyPath))
            {
                var existing = Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());
                if (existing.Length != KeySizeBytes)
                {
                    throw new InvalidOperationException($"Protected preference key at '{_keyPath}' is invalid.");
                }

                _cachedKey = existing;
                return existing;
            }

            var created = new byte[KeySizeBytes];
            RandomNumberGenerator.Fill(created);
            File.WriteAllText(_keyPath, Convert.ToBase64String(created));
            _cachedKey = created;
            return created;
        }
    }
}