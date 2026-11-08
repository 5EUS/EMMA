using System.Security.Cryptography;
using System.Text;

using EMMA.Application.Ports;

namespace EMMA.Infrastructure.Secrets;

/// <summary>
/// An <see cref="ISecretBox"/> that encrypts with AES-256-GCM under a random key kept in a file beside the database.
/// The file is created on first use and readable only by the user on Unix. That protects secrets from anyone who gets
/// a copy of the database (a backup, a synced folder) but not from someone who can read the user's own files; keeping
/// the key in the platform's keychain is a later step. Copy the key file with the database, or its secrets are lost.
/// </summary>
/// <param name="keyPath">Where the key lives.</param>
public sealed class FileSecretBox(string keyPath) : ISecretBox, IDisposable
{
    private const byte Version = 1;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? key;

    /// <inheritdoc/>
    public async Task<string> ProtectAsync(string plaintext, string context, CancellationToken cancellationToken)
    {
        byte[] secretKey = await this.KeyAsync(cancellationToken).ConfigureAwait(false);
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        byte[] sealedBytes = new byte[1 + NonceBytes + TagBytes + data.Length];
        sealedBytes[0] = Version;
        Span<byte> nonce = sealedBytes.AsSpan(1, NonceBytes);
        Span<byte> tag = sealedBytes.AsSpan(1 + NonceBytes, TagBytes);
        Span<byte> cipher = sealedBytes.AsSpan(1 + NonceBytes + TagBytes);
        RandomNumberGenerator.Fill(nonce);

        using AesGcm aes = new(secretKey, TagBytes);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(context));
        CryptographicOperations.ZeroMemory(data);
        return Convert.ToBase64String(sealedBytes);
    }

    /// <inheritdoc/>
    public async Task<string?> UnprotectAsync(string protectedText, string context, CancellationToken cancellationToken)
    {
        byte[] sealedBytes;
        try
        {
            sealedBytes = Convert.FromBase64String(protectedText);
        }
        catch (FormatException)
        {
            return null;
        }

        if (sealedBytes.Length < 1 + NonceBytes + TagBytes || sealedBytes[0] != Version)
        {
            return null;
        }

        byte[] secretKey = await this.KeyAsync(cancellationToken).ConfigureAwait(false);
        byte[] data = new byte[sealedBytes.Length - 1 - NonceBytes - TagBytes];
        try
        {
            using AesGcm aes = new(secretKey, TagBytes);
            aes.Decrypt(
                sealedBytes.AsSpan(1, NonceBytes),
                sealedBytes.AsSpan(1 + NonceBytes + TagBytes),
                sealedBytes.AsSpan(1 + NonceBytes, TagBytes),
                data,
                Encoding.UTF8.GetBytes(context));
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException)
        {
            // A wrong key (the file was replaced), a wrong context or a damaged value: all read as not set.
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => this.gate.Dispose();

    private static async Task<byte[]> LoadOrCreateAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        byte[] created = RandomNumberGenerator.GetBytes(KeyBytes);

        // Write the key somewhere private, then move it into place in one step. Creating the real file and then
        // writing to it would let a process that starts at the same moment (the CLI beside the API) find it empty.
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            FileStream stream = new(temporary, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(created, cancellationToken).ConfigureAwait(false);
            }

            // Fails if another process got there first; then theirs is the key.
            File.Move(temporary, path);
            return created;
        }
        catch (IOException) when (File.Exists(path))
        {
            return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes.Length == KeyBytes
            ? bytes
            : throw new InvalidDataException($"The secret key file '{path}' is damaged (it must be {KeyBytes} bytes). Delete it to start again; stored secrets will be lost.");
    }

    private async Task<byte[]> KeyAsync(CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return this.key ??= await LoadOrCreateAsync(keyPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }
}
