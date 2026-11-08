using Xunit;

namespace EMMA.Domain.Tests;

/// <summary>Tests for <see cref="Outcome{T}"/>.</summary>
public sealed class OutcomeTests
{
    /// <summary>A success exposes its value and has no error.</summary>
    [Fact]
    public void SuccessExposesValue()
    {
        Outcome<int> outcome = Outcome.Success(7);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(7, outcome.Value);
        Assert.Null(outcome.Error);
    }

    /// <summary>Reading the value of a failure is a programming error and throws.</summary>
    [Fact]
    public void FailureValueThrows()
    {
        Outcome<int> outcome = Outcome.Failure<int>(ErrorCode.NotFound, "gone");

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ErrorCode.NotFound, outcome.Error?.Code);
        Assert.Throws<InvalidOperationException>(() => outcome.Value);
    }

    /// <summary>Map transforms successes and passes failures through untouched.</summary>
    [Fact]
    public void MapTransformsOnlySuccesses()
    {
        Outcome<int> failure = Outcome.Failure<int>(ErrorCode.Unavailable, "down");

        Assert.Equal("7", Outcome.Success(7).Map(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Value);
        Assert.Equal(ErrorCode.Unavailable, failure.Map(value => value + 1).Error?.Code);
    }

    /// <summary>Only rate limits and unavailability are transient.</summary>
    /// <param name="code">The failure class.</param>
    /// <param name="transient">Whether retrying can help.</param>
    [Theory]
    [InlineData(ErrorCode.RateLimited, true)]
    [InlineData(ErrorCode.Unavailable, true)]
    [InlineData(ErrorCode.NotFound, false)]
    [InlineData(ErrorCode.Unauthorized, false)]
    [InlineData(ErrorCode.Internal, false)]
    public void TransientClassification(ErrorCode code, bool transient) =>
        Assert.Equal(transient, new PluginError(code, "m").IsTransient);
}
