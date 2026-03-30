namespace EMMA.Plugin.Common;

public enum PluginOperationErrorKind
{
    UnsupportedOperation,
    InvalidArguments,
    Failed
}

public readonly record struct PluginOperationErrorInfo(
    PluginOperationErrorKind Kind,
    string Message);

public static class PluginOperationError
{
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
