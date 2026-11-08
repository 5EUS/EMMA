namespace EMMA.WasmRuntime;

/// <summary>The native Wasm host could not be loaded or created: a deployment problem, not a plugin problem.</summary>
public sealed class WasmRuntimeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="WasmRuntimeException"/> class.</summary>
    public WasmRuntimeException()
        : base("The Wasm host failed.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WasmRuntimeException"/> class.</summary>
    /// <param name="message">What failed.</param>
    public WasmRuntimeException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WasmRuntimeException"/> class.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The cause.</param>
    public WasmRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
