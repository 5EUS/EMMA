using System.Net;
using System.Net.Http.Headers;

using EMMA.Domain;
using EMMA.Infrastructure.Http;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Infrastructure.Tests;

/// <summary>Tests for <see cref="HttpAssetFetcher"/>: policy on every hop, credential handling and limits.</summary>
public sealed class HttpAssetFetcherTests
{
    private static readonly CapabilityPolicy Policy = new(["cdn.example.com", "other.example.com"], cacheAllowed: false);

    private readonly FakeTimeProvider time = new();

    /// <summary>The body and its media type come back.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReturnsBodyAndMime()
    {
        using StubHttpHandler handler = new(_ => Ok([1, 2, 3], "image/jpeg"));

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Equal("image/jpeg", payload.Value.Mime);
        Assert.Equal([1, 2, 3], payload.Value.Payload.ToArray());
        Assert.Equal(this.time.GetUtcNow(), payload.Value.FetchedAt);
    }

    /// <summary>A missing content type falls back to a generic binary type.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task MissingContentTypeFallsBack()
    {
        using StubHttpHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Equal("application/octet-stream", payload.Value.Mime);
    }

    /// <summary>A host outside the policy is refused before any request is sent.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnlistedHostIsBlockedWithoutARequest()
    {
        using StubHttpHandler handler = new(_ => Ok([1], "image/png"));

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("evil.example.net")).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, payload.Error?.Code);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A redirect from a permitted host to a forbidden one is stopped before the second request.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RedirectToAForbiddenHostIsBlocked()
    {
        using StubHttpHandler handler = new(request => Redirect(TestData.Url("evil.example.net", "/steal")));

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, payload.Error?.Code);
        Assert.Single(handler.Requests);
    }

    /// <summary>A permitted redirect is followed, and credentials are dropped when it leaves the original host.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CrossHostRedirectDropsCredentials()
    {
        using StubHttpHandler handler = new(request => request.RequestUri!.Host.StartsWith("cdn", StringComparison.Ordinal)
            ? Redirect(TestData.Url("other.example.com", "/b.png"))
            : Ok([7], "image/png"));
        RemoteDelivery delivery = new(
            TestData.Url("cdn.example.com", "/a.png"),
            [new HttpHeader("Authorization", "Bearer secret"), new HttpHeader("Cookie", "s=1"), new HttpHeader("Referer", "https://example.com/")],
            null);

        Outcome<AssetPayload> payload = await this.Fetch(handler, delivery).ConfigureAwait(true);

        Assert.True(payload.IsSuccess);
        Assert.Contains("Authorization", handler.Requests[0].HeaderNames);
        Assert.DoesNotContain("Authorization", handler.Requests[1].HeaderNames);
        Assert.DoesNotContain("Cookie", handler.Requests[1].HeaderNames);
        Assert.Contains("Referer", handler.Requests[1].HeaderNames);
    }

    /// <summary>A redirect that stays on the same host keeps its credentials, and a relative Location is resolved.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SameHostRedirectKeepsCredentials()
    {
        using StubHttpHandler handler = new(request => string.Equals(request.RequestUri!.AbsolutePath, "/a.png", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/moved.png", UriKind.Relative) } }
            : Ok([7], "image/png"));
        RemoteDelivery delivery = new(TestData.Url("cdn.example.com", "/a.png"), [new HttpHeader("Authorization", "Bearer secret")], null);

        Outcome<AssetPayload> payload = await this.Fetch(handler, delivery).ConfigureAwait(true);

        Assert.True(payload.IsSuccess);
        Assert.Equal("/moved.png", handler.Requests[1].Url.AbsolutePath);
        Assert.Contains("Authorization", handler.Requests[1].HeaderNames);
    }

    /// <summary>An endless redirect chain stops with a failure.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TooManyRedirectsFail()
    {
        using StubHttpHandler handler = new(_ => Redirect(TestData.Url("cdn.example.com", "/again")));

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Unavailable, payload.Error?.Code);
        Assert.Equal(6, handler.Requests.Count);
    }

    /// <summary>A handler that follows redirects on its own is still caught by the final-URL check.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AutoFollowingHandlersAreStillChecked()
    {
        using StubHttpHandler handler = new(_ =>
        {
            HttpResponseMessage response = Ok([1], "image/png");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, TestData.Url("evil.example.net", "/landed"));
            return response;
        });

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, payload.Error?.Code);
    }

    /// <summary>HTTP statuses map to the contract's error classes.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="expected">The expected error class.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, ErrorCode.NotFound)]
    [InlineData(HttpStatusCode.Gone, ErrorCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ErrorCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorCode.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorCode.Unavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, ErrorCode.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, ErrorCode.InvalidArgument)]
    public async Task StatusesMapToErrorClasses(HttpStatusCode status, ErrorCode expected)
    {
        using StubHttpHandler handler = new(_ => new HttpResponseMessage(status));

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Equal(expected, payload.Error?.Code);
    }

    /// <summary>A rate-limit answer carries the upstream's Retry-After.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RateLimitCarriesRetryAfter()
    {
        using StubHttpHandler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Equal(TimeSpan.FromSeconds(7), payload.Error?.RetryAfter);
    }

    /// <summary>A declared length over the limit is refused without reading the body.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DeclaredSizeOverTheLimitIsRefused()
    {
        using StubHttpHandler handler = new(_ => Ok(new byte[100], "image/png"));

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com"), maxBytes: 50).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, payload.Error?.Code);
    }

    /// <summary>A body that lies about, or hides, its size is still cut off at the limit.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StreamingBodyIsCutOffAtTheLimit()
    {
        using StubHttpHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[100]) });

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com"), maxBytes: 50).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, payload.Error?.Code);
    }

    /// <summary>A body exactly at the limit is accepted.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task BodyAtTheLimitIsAccepted()
    {
        using StubHttpHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[50]) });

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com"), maxBytes: 50).ConfigureAwait(true);

        Assert.Equal(50, payload.Value.Payload.Length);
    }

    /// <summary>A transport failure is reported as unavailable.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NetworkFailureIsUnavailable()
    {
        using StubHttpHandler handler = new(_ => throw new HttpRequestException("boom"));

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Unavailable, payload.Error?.Code);
    }

    /// <summary>The message names the host and the innermost cause, so a TLS or DNS problem can be told apart.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NetworkFailureNamesTheCause()
    {
        using StubHttpHandler handler = new(_ => throw new HttpRequestException("outer", new IOException("the certificate was not trusted")));

        Outcome<AssetPayload> payload = await this.Fetch(handler, Delivery("cdn.example.com")).ConfigureAwait(true);

        Assert.Contains("cdn.example.com", payload.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("the certificate was not trusted", payload.Error?.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Ok(byte[] body, string mime)
    {
        ByteArrayContent content = new(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(Uri to) =>
        new(HttpStatusCode.Found) { Headers = { Location = to } };

    private static RemoteDelivery Delivery(string host) => new(TestData.Url(host, "/a.png"), [], null);

    private async Task<Outcome<AssetPayload>> Fetch(StubHttpHandler handler, RemoteDelivery delivery, long maxBytes = 1024)
    {
        using HttpClient client = new(handler, disposeHandler: false);
        HttpAssetFetcher fetcher = new(client, this.time);
        return await fetcher.FetchAsync(delivery, Policy, maxBytes, CancellationToken.None).ConfigureAwait(true);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        private readonly byte[] bytes = bytes;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(this.bytes, 0, this.bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
