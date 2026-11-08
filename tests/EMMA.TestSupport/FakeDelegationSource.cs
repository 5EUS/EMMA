using EMMA.PluginHost.Signing;

namespace EMMA.TestSupport;

/// <summary>An <see cref="IDelegationSource"/> backed by a dictionary that counts how often it is asked.</summary>
public sealed class FakeDelegationSource : IDelegationSource
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    private int calls;

    /// <summary>Gets how many times a file was requested.</summary>
    public int Calls => this.calls;

    /// <summary>Makes a delegation file available.</summary>
    /// <param name="repositoryId">The repository.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>This instance, for chaining.</returns>
    public FakeDelegationSource With(string repositoryId, byte[] bytes)
    {
        this.files[repositoryId] = bytes;
        return this;
    }

    /// <inheritdoc/>
    public Task<ReadOnlyMemory<byte>?> GetAsync(string repositoryId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.calls);

        // Explicit branches: "cond ? bytes : null" would yield a null array, which converts to an empty
        // (non-null) ReadOnlyMemory and make a missing file look like an empty one.
        if (this.files.TryGetValue(repositoryId, out byte[]? bytes))
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(bytes);
        }

        return Task.FromResult<ReadOnlyMemory<byte>?>(null);
    }
}
