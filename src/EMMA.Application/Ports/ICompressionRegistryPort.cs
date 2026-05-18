using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Resolves compression adapters by media type.
/// </summary>
public interface ICompressionRegistryPort
{
    /// <summary>
    /// Returns the adapter that should handle the supplied media type, or null if unsupported.
    /// </summary>
    ICompressionAdapterPort? Resolve(MediaType mediaType);

    /// <summary>
    /// Lists media types with registered compression adapters.
    /// </summary>
    IReadOnlyList<MediaType> ListSupportedMediaTypes();
}