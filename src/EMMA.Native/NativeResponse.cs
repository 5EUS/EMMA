namespace EMMA.Native;

/// <summary>What an operation produced: a JSON envelope and, for reads of bytes, the bytes.</summary>
/// <param name="Json">The UTF-8 JSON envelope, <c>{"ok": ...}</c> or <c>{"error": {...}}</c>.</param>
/// <param name="Blob">Binary content that does not belong in JSON, or <see langword="null"/>.</param>
internal sealed record NativeResponse(byte[] Json, byte[]? Blob = null);
