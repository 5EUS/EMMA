using EMMA.Plugin.Common;

namespace EMMA.Plugin.Common.Tests;

public class PluginOperationRequestExtensionsTests
{
    [Fact]
    public void NormalizedOperation_TrimsAndLowercases()
    {
        var request = new OperationRequest("  SeArCh ", null, null, null, null);

        Assert.Equal("search", request.NormalizedOperation());
    }

    [Fact]
    public void ResolveMediaId_UsesRequestValueBeforeArgsJson()
    {
        const string argsJson = """
        {
          "mediaId": "args-media"
        }
        """;

        var fromRequest = new OperationRequest("page", "request-media", null, argsJson, null);
        var fromArgs = new OperationRequest("page", null, null, argsJson, null);

        Assert.Equal("request-media", fromRequest.ResolveMediaId());
        Assert.Equal("args-media", fromArgs.ResolveMediaId());
    }

    [Fact]
    public void ResolveChapterIdAndIndexes_ReadFromArgsJson()
    {
        const string argsJson = """
        {
          "chapterId": "ch-5",
          "pageIndex": 3,
          "startIndex": "4",
          "count": "6"
        }
        """;

        var request = new OperationRequest("pages", null, null, argsJson, null);

        Assert.Equal("ch-5", request.ResolveChapterId());
        Assert.Equal((uint)3, request.ResolvePageIndex());
        Assert.Equal((uint)4, request.ResolveStartIndex());
        Assert.Equal((uint)6, request.ResolveCount());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("paged", true)]
    [InlineData(" PAGED ", true)]
    [InlineData("video", false)]
    public void IsPagedMediaRequest_HandlesCommonValues(string? mediaType, bool expected)
    {
        var request = new OperationRequest("invoke", null, mediaType, null, null);
        Assert.Equal(expected, request.IsPagedMediaRequest());
    }
}
