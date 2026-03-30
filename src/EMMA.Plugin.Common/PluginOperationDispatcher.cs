namespace EMMA.Plugin.Common;

public sealed class PluginOperationDispatcher
{
    private readonly Dictionary<string, Func<OperationRequest, OperationResult>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public PluginOperationDispatcher Register(string operation, Func<OperationRequest, OperationResult> handler)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("Operation name is required.", nameof(operation));
        }

        _handlers[operation.Trim()] = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    public OperationResult Dispatch(OperationRequest request)
    {
        var operation = request.NormalizedOperation();
        if (!_handlers.TryGetValue(operation, out var handler))
        {
            return OperationResult.UnsupportedOperation(operation);
        }

        try
        {
            return handler(request);
        }
        catch (Exception ex)
        {
            return OperationResult.Failed(ex.Message);
        }
    }
}

public sealed class PluginOperationPayloadRouter
{
    private readonly Dictionary<string, Func<OperationRequest, string?>> _hintResolvers = new(StringComparer.OrdinalIgnoreCase);

    public PluginOperationPayloadRouter Register(string operation, Func<OperationRequest, string?> hintResolver)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("Operation name is required.", nameof(operation));
        }

        _hintResolvers[operation.Trim()] = hintResolver ?? throw new ArgumentNullException(nameof(hintResolver));
        return this;
    }

    public string Resolve(
        OperationRequest request,
        Func<string, string?, string?> payloadProvider,
        bool useArgsJsonFallbackHint = true)
    {
        var normalizedOperation = request.NormalizedOperation();
        var providerOperation = string.IsNullOrWhiteSpace(request.operation)
            ? normalizedOperation
            : request.operation;

        var hint = _hintResolvers.TryGetValue(normalizedOperation, out var hintResolver)
            ? hintResolver(request)
            : useArgsJsonFallbackHint
                ? request.argsJson
                : null;

        return PluginPayload.ResolvePayload(
            request.payloadJson,
            () => payloadProvider(providerOperation, hint));
    }
}
