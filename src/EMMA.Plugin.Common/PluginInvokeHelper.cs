namespace EMMA.Plugin.Common;

public static class PluginInvokeHelper
{
    public static TResult Invoke0<TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation)
    {
        return ((Func<TResult>)wasmDispatch[operation])();
    }

    public static TResult Invoke1<TArg1, TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation, TArg1 arg1)
    {
        return ((Func<TArg1, TResult>)wasmDispatch[operation])(arg1);
    }

    public static TResult Invoke2<TArg1, TArg2, TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation, TArg1 arg1, TArg2 arg2)
    {
        return ((Func<TArg1, TArg2, TResult>)wasmDispatch[operation])(arg1, arg2);
    }

    public static TResult Invoke4<TArg1, TArg2, TArg3, TArg4, TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4)
    {
        return ((Func<TArg1, TArg2, TArg3, TArg4, TResult>)wasmDispatch[operation])(arg1, arg2, arg3, arg4);
    }

    public static TResult Invoke5<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(IReadOnlyDictionary<string, Delegate> wasmDispatch, string operation, TArg1 arg1, TArg2 arg2, TArg3 arg3, TArg4 arg4, TArg5 arg5)
    {
        return ((Func<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>)wasmDispatch[operation])(arg1, arg2, arg3, arg4, arg5);
    }
}