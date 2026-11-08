using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace EMMA.Domain;

/// <summary>
/// Plugin-local node identifier. Opaque to the host and meaningful only together
/// with the <see cref="PluginId"/> that issued it; see <see cref="NodeRef"/>.
/// </summary>
public readonly record struct LocalNodeId
{
    private LocalNodeId(string value) => this.Value = value;

    /// <summary>Gets the identifier text. Empty only for <see langword="default"/>.</summary>
    public string Value { get; } = string.Empty;

    /// <summary>Creates an identifier or throws when the text is not valid.</summary>
    /// <param name="value">The identifier text.</param>
    /// <returns>The validated identifier.</returns>
    /// <exception cref="ArgumentException">The text is empty or longer than the contract allows.</exception>
    public static LocalNodeId Create(string value) =>
        TryCreate(value, out LocalNodeId id)
            ? id
            : throw new ArgumentException("A node id must be non-empty and at most 512 UTF-8 bytes.", nameof(value));

    /// <summary>Validates and creates an identifier without throwing.</summary>
    /// <param name="value">The candidate text.</param>
    /// <param name="id">The identifier when valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryCreate([NotNullWhen(true)] string? value, out LocalNodeId id)
    {
        if (!string.IsNullOrEmpty(value)
            && value.Length <= ContractLimits.MaxNodeIdBytes
            && Encoding.UTF8.GetByteCount(value) <= ContractLimits.MaxNodeIdBytes)
        {
            id = new LocalNodeId(value);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => this.Value;
}
