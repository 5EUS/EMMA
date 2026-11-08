namespace EMMA.Api.Configuration;

/// <summary>The host's configuration is invalid. The message says what to fix; the host does not start.</summary>
internal sealed class ApiConfigurationException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="ApiConfigurationException"/> class.</summary>
    public ApiConfigurationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ApiConfigurationException"/> class.</summary>
    /// <param name="message">What is wrong.</param>
    public ApiConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ApiConfigurationException"/> class.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ApiConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
