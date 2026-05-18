namespace EMMA.Plugin.Common;

/// <summary>
/// Classifies serialized operation errors.
/// </summary>
public enum PluginOperationErrorKind
{
    /// <summary>
    /// The requested operation is not supported.
    /// </summary>
    UnsupportedOperation,

    /// <summary>
    /// The supplied arguments are invalid.
    /// </summary>
    InvalidArguments,

    /// <summary>
    /// The operation failed during execution.
    /// </summary>
    Failed
}

/// <summary>
/// Represents a parsed operation error.
/// </summary>
/// <param name="Kind">The classified error kind.</param>
/// <param name="Message">The human-readable error message.</param>
public readonly record struct PluginOperationErrorInfo(
    PluginOperationErrorKind Kind,
    string Message);

/// <summary>
/// Parses and formats operation error payloads.
/// </summary>
public static class PluginOperationError
{
    /// <summary>
    /// Parses a serialized operation error string into a structured error value.
    /// </summary>
    /// <param name="value">The serialized error string to parse.</param>
    /// <param name="error">When this method returns, contains the parsed error information if parsing succeeded.</param>
    /// <returns><see langword="true"/> when a non-empty error value was parsed; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out PluginOperationErrorInfo error)
    {
        var message = value?.Trim() ?? string.Empty;
        if (message.Length == 0)
        {
            error = default;
            return false;
        }

        if (TryParsePrefixed(message, "unsupported-operation:", PluginOperationErrorKind.UnsupportedOperation, out error))
        {
            return true;
        }

        if (TryParsePrefixed(message, "invalid-arguments:", PluginOperationErrorKind.InvalidArguments, out error))
        {
            return true;
        }

        if (TryParsePrefixed(message, "failed:", PluginOperationErrorKind.Failed, out error))
        {
            return true;
        }

        error = new PluginOperationErrorInfo(PluginOperationErrorKind.Failed, message);
        return true;
    }

    private static bool TryParsePrefixed(
        string message,
        string prefix,
        PluginOperationErrorKind kind,
        out PluginOperationErrorInfo error)
    {
        if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = default;
            return false;
        }

        var details = message[prefix.Length..].Trim();
        error = new PluginOperationErrorInfo(kind, details);
        return true;
    }
}
