using EMMA.Contracts.Api.V1;

namespace EMMA.Api.Dtos;

public sealed record ApiMediaSummary(
    string Id,
    string Source,
    string Title,
    ApiMediaType MediaType);

public sealed record ApiMediaChapter(
    string Id,
    int Number,
    string Title);

public sealed record ApiMediaPage(
    string Id,
    int Index,
    string ContentUri);

public sealed record ApiPageAsset(
    string ContentType,
    byte[] Payload);
