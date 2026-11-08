namespace EMMA.Native.Requests;

/// <summary>A byte range.</summary>
/// <param name="Start">The first byte.</param>
/// <param name="Length">The number of bytes; omitted means to the end.</param>
internal sealed record RangeCall(
    long? Start,
    long? Length);
