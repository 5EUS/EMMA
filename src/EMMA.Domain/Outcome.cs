namespace EMMA.Domain;

/// <summary>Factory methods for <see cref="Outcome{T}"/>.</summary>
public static class Outcome
{
    /// <summary>Creates a successful outcome.</summary>
    /// <typeparam name="T">The success value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The outcome.</returns>
    public static Outcome<T> Success<T>(T value) => new(value, null);

    /// <summary>Creates a failed outcome.</summary>
    /// <typeparam name="T">The success value type.</typeparam>
    /// <param name="error">The failure.</param>
    /// <returns>The outcome.</returns>
    public static Outcome<T> Failure<T>(PluginError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Outcome<T>(default, error);
    }

    /// <summary>Creates a failed outcome from a code and message.</summary>
    /// <typeparam name="T">The success value type.</typeparam>
    /// <param name="code">The failure class.</param>
    /// <param name="message">The message.</param>
    /// <returns>The outcome.</returns>
    public static Outcome<T> Failure<T>(ErrorCode code, string message) => Failure<T>(new PluginError(code, message));
}
