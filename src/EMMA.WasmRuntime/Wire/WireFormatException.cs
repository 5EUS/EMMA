namespace EMMA.WasmRuntime.Wire;

/// <summary>The plugin (or the host) produced data that does not follow the contract.</summary>
public sealed class WireFormatException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="WireFormatException"/> class.</summary>
    public WireFormatException()
        : base("The data does not follow the contract.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WireFormatException"/> class.</summary>
    /// <param name="message">What was wrong.</param>
    public WireFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WireFormatException"/> class.</summary>
    /// <param name="message">What was wrong.</param>
    /// <param name="innerException">The cause.</param>
    public WireFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
