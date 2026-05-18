using System.Collections.Concurrent;
using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>
/// In-memory registry for compression adapters.
/// </summary>
public sealed class InMemoryCompressionRegistryPort : ICompressionRegistryPort
{
    private readonly ConcurrentDictionary<MediaType, ICompressionAdapterPort> _adapters = new();

    public InMemoryCompressionRegistryPort(params ICompressionAdapterPort[] adapters)
    {
        foreach (var adapter in adapters)
        {
            Register(adapter);
        }
    }

    public ICompressionAdapterPort? Resolve(MediaType mediaType)
        => _adapters.TryGetValue(mediaType, out var adapter) ? adapter : null;

    public IReadOnlyList<MediaType> ListSupportedMediaTypes()
        => [.. _adapters.Keys.OrderBy(static mediaType => mediaType)];

    public void Register(ICompressionAdapterPort adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        foreach (var mediaType in adapter.SupportedMediaTypes)
        {
            _adapters[mediaType] = adapter;
        }
    }
}