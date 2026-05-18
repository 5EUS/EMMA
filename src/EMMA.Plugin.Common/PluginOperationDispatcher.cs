namespace EMMA.Plugin.Common;

/// <summary>
/// Dispatches operation requests to registered handlers.
/// </summary>
public sealed class PluginOperationDispatcher
{
    private readonly Dictionary<string, Func<OperationRequest, OperationResult>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a handler for a normalized operation name.
    /// </summary>
    /// <param name="operation">The operation name to handle.</param>
    /// <param name="handler">The handler that executes the operation.</param>
    /// <returns>The current dispatcher instance.</returns>
    public PluginOperationDispatcher Register(string operation, Func<OperationRequest, OperationResult> handler)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("Operation name is required.", nameof(operation));
        }

        _handlers[operation.Trim()] = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Dispatches an operation request to the registered handler for its normalized operation name.
    /// </summary>
    /// <param name="request">The operation request to dispatch.</param>
    /// <returns>The handler result, or an error result when no handler exists or execution fails.</returns>
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

/// <summary>
/// Resolves payload hints and fallback payloads for operation requests.
/// </summary>
public sealed class PluginOperationPayloadRouter
{
    private readonly Dictionary<string, Func<OperationRequest, string?>> _hintResolvers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a payload hint resolver for an operation.
    /// </summary>
    /// <param name="operation">The operation name to associate with the resolver.</param>
    /// <param name="hintResolver">The resolver that produces a payload hint from the request.</param>
    /// <returns>The current router instance.</returns>
    public PluginOperationPayloadRouter Register(string operation, Func<OperationRequest, string?> hintResolver)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("Operation name is required.", nameof(operation));
        }

        _hintResolvers[operation.Trim()] = hintResolver ?? throw new ArgumentNullException(nameof(hintResolver));
        return this;
    }

    /// <summary>
    /// Resolves the effective payload for a request from the request body, a registered hint resolver, or the request arguments.
    /// </summary>
    /// <param name="request">The operation request whose payload should be resolved.</param>
    /// <param name="payloadProvider">The callback that fetches payload content from an operation name and optional hint.</param>
    /// <param name="useArgsJsonFallbackHint">When set to <see langword="true"/>, uses <see cref="OperationRequest.argsJson"/> as the fallback hint.</param>
    /// <returns>The resolved payload content.</returns>
    public string Resolve(
        OperationRequest request,
        Func<string, string?, string?> payloadProvider,
        bool useArgsJsonFallbackHint = true)
    {
        var providedPayload = PluginPayload.NormalizePayload(request.payloadJson);
        if (!string.IsNullOrWhiteSpace(providedPayload))
        {
            return providedPayload;
        }

        var normalizedOperation = request.NormalizedOperation();
        var providerOperation = string.IsNullOrWhiteSpace(request.operation)
            ? normalizedOperation
            : request.operation;

        try
        {
            var hint = _hintResolvers.TryGetValue(normalizedOperation, out var hintResolver)
                ? hintResolver(request)
                : useArgsJsonFallbackHint
                    ? request.argsJson
                    : null;

            return PluginPayload.ResolvePayload(
                null,
                () => payloadProvider(providerOperation, hint));
        }
        catch
        {
            return string.Empty;
        }
    }
}
