using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Native;

/// <summary>
/// One open handle: a runtime and the user it belongs to. Closing it cancels calls in flight and waits for them,
/// so the runtime is never disposed under a running operation.
/// </summary>
/// <param name="runtime">The assembled EMMA.</param>
/// <param name="user">The user whose progress and library the handle reads and writes.</param>
internal sealed class NativeSession(EmmaRuntime runtime, UserId user) : IAsyncDisposable
{
    private readonly CancellationTokenSource closing = new();
    private readonly TaskCompletionSource idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private int inFlight;
    private bool closed;

    /// <summary>Prepares the runtime: creates the database, the cache and each plugin's permissions.</summary>
    /// <returns>A task that completes when the runtime is ready.</returns>
    public Task InitializeAsync() => runtime.InitializeAsync(CancellationToken.None);

    /// <summary>Runs an operation. It never throws: a failure becomes an error envelope.</summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="request">The UTF-8 JSON request.</param>
    /// <returns>The response.</returns>
    public async Task<NativeResponse> InvokeAsync(string operation, ReadOnlyMemory<byte> request)
    {
        if (!this.TryEnter())
        {
            return Envelope.Fault("closed", "The runtime was closed.");
        }

        try
        {
            return await Dispatcher.InvokeAsync(operation, new OperationContext(runtime, user, request, this.closing.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Envelope.Fault("closed", "The runtime was closed.");
        }
        catch (Exception ex)
        {
            // The boundary of the library: nothing may escape into the caller's process. The details are a bug to report, not to hand out.
            NativeDiagnostics.Report(ex);
            return Envelope.Fault("internal_error", "The call failed.");
        }
        finally
        {
            this.Leave();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        lock (this.gate)
        {
            this.closed = true;
            if (this.inFlight == 0)
            {
                this.idle.TrySetResult();
            }
        }

        await this.closing.CancelAsync().ConfigureAwait(false);
        await this.idle.Task.ConfigureAwait(false);

        // The session owns the runtime it was handed: it is built for it and nothing else holds it.
#pragma warning disable IDISP007
        await runtime.DisposeAsync().ConfigureAwait(false);
#pragma warning restore IDISP007
        this.closing.Dispose();
    }

    private bool TryEnter()
    {
        lock (this.gate)
        {
            if (this.closed)
            {
                return false;
            }

            this.inFlight++;
            return true;
        }
    }

    private void Leave()
    {
        lock (this.gate)
        {
            this.inFlight--;
            if (this.closed && this.inFlight == 0)
            {
                this.idle.TrySetResult();
            }
        }
    }
}
