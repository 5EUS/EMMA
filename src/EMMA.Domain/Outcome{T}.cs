using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace EMMA.Domain;

/// <summary>
/// The result of an operation whose failure is an expected outcome (not found, rate
/// limited, unauthorized) rather than a bug. Exceptions stay reserved for bugs and
/// infrastructure faults.
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
[StructLayout(LayoutKind.Auto)]
public readonly struct Outcome<T> : IEquatable<Outcome<T>>
{
    private readonly T? value;
    private readonly PluginError? error;

    /// <summary>Initializes a new instance of the <see cref="Outcome{T}"/> struct. Use <see cref="Outcome"/> factories.</summary>
    /// <param name="value">The success value.</param>
    /// <param name="error">The failure.</param>
    internal Outcome(T? value, PluginError? error)
    {
        this.value = value;
        this.error = error;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => this.error is null;

    /// <summary>Gets the success value.</summary>
    /// <exception cref="InvalidOperationException">The outcome is a failure.</exception>
    public T Value => this.error is null
        ? this.value!
        : throw new InvalidOperationException($"Outcome failed: {this.error.Message}");

    /// <summary>Gets the failure, or <see langword="null"/> on success.</summary>
    public PluginError? Error => this.error;

    /// <summary>Compares two outcomes.</summary>
    /// <param name="left">The first outcome.</param>
    /// <param name="right">The second outcome.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(Outcome<T> left, Outcome<T> right) => left.Equals(right);

    /// <summary>Compares two outcomes.</summary>
    /// <param name="left">The first outcome.</param>
    /// <param name="right">The second outcome.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(Outcome<T> left, Outcome<T> right) => !left.Equals(right);

    /// <summary>Transforms the success value, passing failures through.</summary>
    /// <typeparam name="TResult">The mapped value type.</typeparam>
    /// <param name="map">The mapping.</param>
    /// <returns>The mapped outcome.</returns>
    public Outcome<TResult> Map<TResult>(Func<T, TResult> map)
    {
        return this.error is null
            ? Outcome.Success(map(this.value!))
            : Outcome.Failure<TResult>(this.error);
    }

    /// <inheritdoc/>
    public bool Equals(Outcome<T> other) =>
        EqualityComparer<T?>.Default.Equals(this.value, other.value) && Equals(this.error, other.error);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Outcome<T> other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.value, this.error);
}
