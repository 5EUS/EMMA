namespace EMMA.Plugin.Common;

/// <summary>
/// Invokes typed delegates stored in the WASM operation dispatch table.
/// </summary>
public static class PluginInvokeHelper
{
    /// <summary>
    /// Invokes a zero-argument WASM delegate registered for the specified operation.
    /// </summary>
    /// <param name="wasmDispatch">The delegate lookup keyed by operation name.</param>
    /// <param name="operation">The operation whose delegate should be invoked.</param>
    /// <returns>The delegate result.</returns>
    public static TResult Invoke0<TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation)
    {
        return ((Func<TResult>)wasmDispatch[operation])();
    }

    /// <summary>
    /// Invokes a single-argument WASM delegate registered for the specified operation.
    /// </summary>
    /// <param name="wasmDispatch">The delegate lookup keyed by operation name.</param>
    /// <param name="operation">The operation whose delegate should be invoked.</param>
    /// <param name="arg1">The first delegate argument.</param>
    /// <returns>The delegate result.</returns>
    public static TResult Invoke1<TArg1, TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation, TArg1 arg1)
    {
        return ((Func<TArg1, TResult>)wasmDispatch[operation])(arg1);
    }

    /// <summary>
    /// Invokes a two-argument WASM delegate registered for the specified operation.
    /// </summary>
    /// <param name="wasmDispatch">The delegate lookup keyed by operation name.</param>
    /// <param name="operation">The operation whose delegate should be invoked.</param>
    /// <param name="arg1">The first delegate argument.</param>
    /// <param name="arg2">The second delegate argument.</param>
    /// <returns>The delegate result.</returns>
    public static TResult Invoke2<TArg1, TArg2, TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation, TArg1 arg1, TArg2 arg2)
    {
        return ((Func<TArg1, TArg2, TResult>)wasmDispatch[operation])(arg1, arg2);
    }

    /// <summary>
    /// Invokes a four-argument WASM delegate registered for the specified operation.
    /// </summary>
    /// <param name="wasmDispatch">The delegate lookup keyed by operation name.</param>
    /// <param name="operation">The operation whose delegate should be invoked.</param>
    /// <param name="arg1">The first delegate argument.</param>
    /// <param name="arg2">The second delegate argument.</param>
    /// <param name="arg3">The third delegate argument.</param>
    /// <param name="arg4">The fourth delegate argument.</param>
    /// <returns>The delegate result.</returns>
    public static TResult Invoke4<TArg1, TArg2, TArg3, TArg4, TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4)
    {
        return ((Func<TArg1, TArg2, TArg3, TArg4, TResult>)wasmDispatch[operation])(arg1, arg2, arg3, arg4);
    }

    /// <summary>
    /// Invokes a five-argument WASM delegate registered for the specified operation.
    /// </summary>
    /// <param name="wasmDispatch">The delegate lookup keyed by operation name.</param>
    /// <param name="operation">The operation whose delegate should be invoked.</param>
    /// <param name="arg1">The first delegate argument.</param>
    /// <param name="arg2">The second delegate argument.</param>
    /// <param name="arg3">The third delegate argument.</param>
    /// <param name="arg4">The fourth delegate argument.</param>
    /// <param name="arg5">The fifth delegate argument.</param>
    /// <returns>The delegate result.</returns>
    public static TResult Invoke5<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5)
    {
        return ((Func<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>)wasmDispatch[operation])(arg1, arg2, arg3, arg4, arg5);
    }
}