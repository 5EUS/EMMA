using EMMA.Application.Ports;

namespace EMMA.TestSupport;

/// <summary>An in-memory <see cref="IDownloadFilePort"/>.</summary>
public sealed class FakeDownloadFiles : IDownloadFilePort
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, byte[]> files = [];

    /// <summary>Gets the names of the stored files.</summary>
    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.files.Keys];
            }
        }
    }

    /// <inheritdoc/>
    public Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            this.files[name] = bytes.ToArray();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.files.GetValueOrDefault(name));
        }
    }

    /// <inheritdoc/>
    public Task DeleteAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            foreach (string name in names)
            {
                this.files.Remove(name);
            }
        }

        return Task.CompletedTask;
    }
}
