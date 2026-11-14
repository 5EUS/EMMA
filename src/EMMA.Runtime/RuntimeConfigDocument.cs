namespace EMMA.Runtime;

/// <summary>The JSON shape of <see cref="RuntimeConfig"/>.</summary>
/// <param name="DataDir">The directory EMMA keeps its state in.</param>
/// <param name="User">The user id; defaults to <c>local</c>.</param>
/// <param name="Trust">Which plugin signatures are accepted.</param>
/// <param name="NativeDir">The directory holding the native libraries, when they are not next to the executable.</param>
/// <param name="CacheDir">The directory for purgeable cache files; defaults to <c>cache</c> inside <c>data_dir</c>.</param>
internal sealed record RuntimeConfigDocument(string? DataDir, string? User, TrustDocument? Trust, string? NativeDir, string? CacheDir = null);
