using System.Text;
using System.Text.Json.Nodes;

using Xunit;

namespace EMMA.Native.Tests;

/// <summary>Tests for creating, using and closing handles, and for how misuse is reported.</summary>
public sealed class NativeApiTests
{
    /// <summary>A configuration without a data directory, or with an unknown field, is refused with an error envelope and no handle.</summary>
    /// <param name="config">The configuration.</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData(@"{""data_dir"":""/tmp/x"",""surprise"":1}")]
    [InlineData(@"{""data_dir"":""/tmp/x"",""user"":""""}")]
    [InlineData(@"{""data_dir"":""/tmp/x"",""trust"":{""repositories"":[{""id"":""r""}]}}")]
    public void BadConfigurationIsRefused(string config)
    {
        int status = NativeApi.Create(Encoding.UTF8.GetBytes(config), out nint handle, out NativeResponse? error);

        Assert.Equal(NativeStatus.InvalidArgument, status);
        Assert.Equal(0, handle);
        Assert.Equal("invalid_argument", JsonNode.Parse(error!.Json)!["error"]!["code"]!.GetValue<string>());
    }

    /// <summary>A handle works until it is closed; then it is an error, and closing again or closing nonsense is harmless.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClosedAndInventedHandlesAreErrors()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            Assert.Equal(NativeStatus.Ok, host.Call("plugins.list").Status);

            NativeApi.Close(host.Handle);
            NativeApi.Close(host.Handle);
            NativeApi.Close(0);
            CallResult closed = host.Call("plugins.list");
            (int invented, _) = NativeApi.Invoke(987654, "plugins.list", []);

            Assert.Equal(NativeStatus.InvalidArgument, closed.Status);
            Assert.Equal("invalid_handle", closed.ErrorCode);
            Assert.Equal(NativeStatus.InvalidArgument, invented);
        }
    }

    /// <summary>Handles are numbers that are never reused, so a stale one cannot reach a newer runtime.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task HandlesAreNotReused()
    {
        NativeHarness first = await NativeHarness.StartAsync().ConfigureAwait(true);
        nint firstHandle = first.Handle;
        await first.DisposeAsync().ConfigureAwait(true);
        NativeHarness second = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (second.ConfigureAwait(true))
        {
            Assert.NotEqual(firstHandle, second.Handle);
            (int status, NativeResponse stale) = NativeApi.Invoke(firstHandle, "plugins.list", []);
            Assert.Equal(NativeStatus.InvalidArgument, status);
            Assert.Equal("invalid_handle", JsonNode.Parse(stale.Json)!["error"]!["code"]!.GetValue<string>());
        }
    }

    /// <summary>An unknown operation and a malformed request are operation failures, not crashes.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task UnknownOperationsAndBadRequestsAreErrors()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            CallResult unknown = host.Call("no.such.operation");
            CallResult unknownField = host.Call("progress.get", new JsonObject { ["nope"] = 1 });
            CallResult badRef = host.Call("progress.get", new JsonObject { ["ref"] = "no-colon" });
            (int status, NativeResponse notJson) = NativeApi.Invoke(host.Handle, "progress.get", "{ nope"u8.ToArray());

            Assert.Equal("unsupported", unknown.ErrorCode);
            Assert.Equal("invalid_argument", unknownField.ErrorCode);
            Assert.Equal("invalid_argument", badRef.ErrorCode);
            Assert.Equal(NativeStatus.Ok, status);
            Assert.Equal("invalid_argument", JsonNode.Parse(notJson.Json)!["error"]!["code"]!.GetValue<string>());
        }
    }

    /// <summary>Every operation the dispatcher knows is documented, and every documented operation exists, so the ABI doc cannot drift.</summary>
    [Fact]
    public void OperationsMatchTheDocumentation()
    {
        string document = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "architecture", "native-abi.md"));

        string[] undocumented = [.. Dispatcher.Operations.Where(operation => !document.Contains($"`{operation}`", StringComparison.Ordinal))];

        Assert.Empty(undocumented);
    }

    /// <summary>Many calls at once on one handle all complete, and closing during them leaves none hanging.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CallsInFlightWhenClosedFinish()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        Task<(int Status, NativeResponse Response)>[] calls =
        [
            .. Enumerable.Range(0, 50).Select(_ => NativeApi.CallAsync(host.Handle, "plugins.list", [])),
        ];
        await NativeApi.DestroyAsync(host.Handle).ConfigureAwait(true);
        (int Status, NativeResponse Response)[] results = await Task.WhenAll(calls).ConfigureAwait(true);
        await host.DisposeAsync().ConfigureAwait(true);

        Assert.All(results, result => Assert.True(result.Status is NativeStatus.Ok or NativeStatus.InvalidArgument));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EMMA.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("EMMA.slnx was not found above the test output.");
    }
}
