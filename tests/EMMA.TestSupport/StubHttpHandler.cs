namespace EMMA.TestSupport;

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate and records every request it sees.</summary>
/// <param name="respond">Produces the response for a request.</param>
public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> requests = [];

    /// <summary>Gets the requests seen so far, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => this.requests;

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.requests.Add(new RecordedRequest(
            request.RequestUri!,
            [.. request.Headers.Select(header => header.Key)]));
        return Task.FromResult(respond(request));
    }
}
