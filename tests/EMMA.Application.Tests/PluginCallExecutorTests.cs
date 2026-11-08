using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for the retry and timeout behaviour around plugin calls.</summary>
public sealed class PluginCallExecutorTests
{
    private static readonly PluginCallOptions Options =
        new(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

    private readonly FakeTimeProvider time = new();

    /// <summary>A success on the first attempt is returned without retrying.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SuccessIsNotRetried()
    {
        int calls = 0;
        PluginCallExecutor executor = new(Options, this.time);

        Outcome<int> outcome = await executor.ExecuteAsync(
            _ =>
            {
                calls++;
                return Task.FromResult(Outcome.Success(7));
            },
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(7, outcome.Value);
        Assert.Equal(1, calls);
    }

    /// <summary>A transient failure is retried until it succeeds.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TransientFailureIsRetried()
    {
        int calls = 0;
        PluginCallExecutor executor = new(Options, this.time);

        Outcome<int> outcome = await TimeRunner.RunAsync(
            executor.ExecuteAsync(
                _ =>
                {
                    calls++;
                    return Task.FromResult(calls < 3
                        ? Outcome.Failure<int>(ErrorCode.Unavailable, "down")
                        : Outcome.Success(calls));
                },
                CancellationToken.None),
            this.time);

        Assert.Equal(3, outcome.Value);
    }

    /// <summary>A permanent failure returns immediately.</summary>
    /// <param name="code">The failure class.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(ErrorCode.NotFound)]
    [InlineData(ErrorCode.Unauthorized)]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.Internal)]
    public async Task PermanentFailureIsNotRetried(ErrorCode code)
    {
        int calls = 0;
        PluginCallExecutor executor = new(Options, this.time);

        Outcome<int> outcome = await executor.ExecuteAsync(
            _ =>
            {
                calls++;
                return Task.FromResult(Outcome.Failure<int>(code, "no"));
            },
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(code, outcome.Error?.Code);
        Assert.Equal(1, calls);
    }

    /// <summary>After the last attempt the last transient failure is returned.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        int calls = 0;
        PluginCallExecutor executor = new(Options, this.time);

        Outcome<int> outcome = await TimeRunner.RunAsync(
            executor.ExecuteAsync(
                _ =>
                {
                    calls++;
                    return Task.FromResult(Outcome.Failure<int>(ErrorCode.RateLimited, "slow down"));
                },
                CancellationToken.None),
            this.time);

        Assert.Equal(ErrorCode.RateLimited, outcome.Error?.Code);
        Assert.Equal(3, calls);
    }

    /// <summary>The delay a plugin asks for is honoured, but never longer than the maximum.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RetryAfterIsHonouredAndCapped()
    {
        int calls = 0;
        PluginCallExecutor executor = new(Options, this.time);
        PluginError tooLong = new(ErrorCode.RateLimited, "later", TimeSpan.FromHours(1));

        Task<Outcome<int>> work = executor.ExecuteAsync(
            _ =>
            {
                calls++;
                return Task.FromResult(calls == 1 ? Outcome.Failure<int>(tooLong) : Outcome.Success(calls));
            },
            CancellationToken.None);

        await Task.Yield();
        this.time.Advance(TimeSpan.FromSeconds(9));
        await Task.Yield();
        Assert.Equal(1, calls);

        this.time.Advance(TimeSpan.FromSeconds(2));
        Outcome<int> outcome = await work.ConfigureAwait(true);

        Assert.Equal(2, outcome.Value);
    }

    /// <summary>An attempt that outlives its timeout becomes an "unavailable" failure and is retried.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SlowAttemptTimesOut()
    {
        int calls = 0;
        PluginCallExecutor executor = new(Options, this.time);

        Outcome<int> outcome = await TimeRunner.RunAsync(
            executor.ExecuteAsync(
                async token =>
                {
                    calls++;
                    await Task.Delay(Timeout.InfiniteTimeSpan, this.time, token).ConfigureAwait(true);
                    return Outcome.Success(1);
                },
                CancellationToken.None),
            this.time);

        Assert.Equal(ErrorCode.Unavailable, outcome.Error?.Code);
        Assert.Equal(3, calls);
    }

    /// <summary>Cancelling the caller's token throws and is never retried.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CallerCancellationPropagates()
    {
        int calls = 0;
        PluginCallExecutor executor = new(Options, this.time);
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            token =>
            {
                calls++;
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Outcome.Success(1));
            },
            cancel.Token));

        Assert.Equal(1, calls);
    }
}
