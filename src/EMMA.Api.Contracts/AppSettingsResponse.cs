namespace EMMA.Api.Contracts;

/// <summary>What the app remembers for the caller under one scope.</summary>
/// <param name="Scope">The scope.</param>
/// <param name="Values">The remembered text by key; empty when nothing is.</param>
public sealed record AppSettingsResponse(
    string Scope,
    IReadOnlyDictionary<string, string> Values);
