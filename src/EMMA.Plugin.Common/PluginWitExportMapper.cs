using System.Diagnostics.CodeAnalysis;

namespace EMMA.Plugin.Common;

/// <summary>
/// Generic base class for WIT component export type mapping.
/// Reduces boilerplate in WASM plugins by providing reusable mapping patterns.
/// </summary>
[RequiresUnreferencedCode("Uses dynamic binding and is not trim-safe. Prefer PluginWitExportMapper<TDomain, TWitExport> for trimming-compatible plugin code.")]
[RequiresDynamicCode("Uses dynamic binding and is not AOT-safe. Prefer PluginWitExportMapper<TDomain, TWitExport> for AOT-compatible plugin code.")]
public abstract class PluginWitExportMapper<TExportSet>
    where TExportSet : class
{
    /// <summary>
    /// Map a single domain item to WIT export type.
    /// </summary>
    protected abstract object MapToWit(dynamic domainItem);

    /// <summary>
    /// Map a single WIT export type to domain item.
    /// </summary>
    protected abstract object MapFromWit(TExportSet witItem);

    /// <summary>
    /// Map a list of domain items to WIT export types.
    /// </summary>
    /// <param name="domainItems">The domain items to map.</param>
    /// <returns>The mapped WIT export values.</returns>
    [RequiresUnreferencedCode("Uses dynamic binding and is not trim-safe. Prefer PluginWitExportMapper<TDomain, TWitExport> for trimming-compatible plugin code.")]
    [RequiresDynamicCode("Uses dynamic binding and is not AOT-safe. Prefer PluginWitExportMapper<TDomain, TWitExport> for AOT-compatible plugin code.")]
    public List<object> MapListToWit(IReadOnlyList<dynamic> domainItems)
    {
        if (domainItems == null || domainItems.Count == 0)
        {
            return [];
        }

        var results = new List<object>(domainItems.Count);
        foreach (var item in domainItems)
        {
            results.Add(MapToWit(item));
        }

        return results;
    }

    /// <summary>
    /// Map a list of WIT export types to domain items.
    /// </summary>
    /// <param name="witItems">The WIT export values to map.</param>
    /// <returns>The mapped domain values.</returns>
    public List<object> MapListFromWit(IReadOnlyList<TExportSet> witItems)
    {
        if (witItems == null || witItems.Count == 0)
        {
            return [];
        }

        var results = new List<object>(witItems.Count);
        foreach (var item in witItems)
        {
            results.Add(MapFromWit(item));
        }

        return results;
    }

    /// <summary>
    /// Map a nullable domain item to WIT export type.
    /// </summary>
    /// <param name="domainItem">The optional domain item to map.</param>
    /// <returns>The mapped WIT export value when present; otherwise, <see langword="null"/>.</returns>
    [RequiresUnreferencedCode("Uses dynamic binding and is not trim-safe. Prefer PluginWitExportMapper<TDomain, TWitExport> for trimming-compatible plugin code.")]
    [RequiresDynamicCode("Uses dynamic binding and is not AOT-safe. Prefer PluginWitExportMapper<TDomain, TWitExport> for AOT-compatible plugin code.")]
    public object? MapNullableToWit(dynamic? domainItem)
    {
        return domainItem == null ? null : MapToWit(domainItem);
    }

    /// <summary>
    /// Map a nullable WIT export type to domain item.
    /// </summary>
    /// <param name="witItem">The optional WIT export value to map.</param>
    /// <returns>The mapped domain value when present; otherwise, <see langword="null"/>.</returns>
    public object? MapNullableFromWit(TExportSet? witItem)
    {
        return witItem == null ? null : MapFromWit(witItem);
    }

    /// <summary>
    /// Helper to safely extract metadata from domain items.
    /// </summary>
    [RequiresUnreferencedCode("Uses dynamic binding and is not trim-safe. Prefer PluginWitExportMapper<TDomain, TWitExport> for trimming-compatible plugin code.")]
    [RequiresDynamicCode("Uses dynamic binding and is not AOT-safe. Prefer PluginWitExportMapper<TDomain, TWitExport> for AOT-compatible plugin code.")]
    protected List<TMetadata> ExtractMetadata<TMetadata>(
        dynamic domainItem,
        Func<dynamic, TMetadata> selector)
    {
        var results = new List<TMetadata>();
        if (domainItem?.metadata is null)
        {
            return results;
        }

        foreach (var meta in domainItem.metadata)
        {
            results.Add(selector(meta));
        }

        return results;
    }

    /// <summary>
    /// Helper to convert unchecked integers to WIT uint.
    /// </summary>
    protected static uint ToWitUint(int value)
    {
        return checked((uint)value);
    }

    /// <summary>
    /// Helper to convert unchecked longs to WIT uint64.
    /// </summary>
    protected static ulong ToWitUint64(long value)
    {
        return checked((ulong)value);
    }

    /// <summary>
    /// Helper to safely cast enums to WIT export types.
    /// </summary>
    protected static TEnum CastEnum<TEnum>(int value)
        where TEnum : struct, Enum
    {
        return (TEnum)Enum.ToObject(typeof(TEnum), value);
    }
}

/// <summary>
/// Strongly-typed base for WIT export mappers with explicit domain and WIT types.
/// </summary>
public abstract class PluginWitExportMapper<TDomain, TWitExport>
    where TDomain : class
    where TWitExport : class
{
    /// <summary>
    /// Map domain item to WIT export type.
    /// </summary>
    protected abstract TWitExport MapToWit(TDomain domainItem);

    /// <summary>
    /// Map WIT export type to domain item.
    /// </summary>
    protected abstract TDomain MapFromWit(TWitExport witItem);

    /// <summary>
    /// Map list of domain items to WIT export types.
    /// </summary>
    /// <param name="domainItems">The domain items to map.</param>
    /// <returns>The mapped WIT export values.</returns>
    public List<TWitExport> MapListToWit(IReadOnlyList<TDomain> domainItems)
    {
        if (domainItems == null || domainItems.Count == 0)
        {
            return [];
        }

        var results = new List<TWitExport>(domainItems.Count);
        foreach (var item in domainItems)
        {
            results.Add(MapToWit(item));
        }

        return results;
    }

    /// <summary>
    /// Map list of WIT export types to domain items.
    /// </summary>
    /// <param name="witItems">The WIT export values to map.</param>
    /// <returns>The mapped domain values.</returns>
    public List<TDomain> MapListFromWit(IReadOnlyList<TWitExport> witItems)
    {
        if (witItems == null || witItems.Count == 0)
        {
            return [];
        }

        var results = new List<TDomain>(witItems.Count);
        foreach (var item in witItems)
        {
            results.Add(MapFromWit(item));
        }

        return results;
    }

    /// <summary>
    /// Map nullable domain item to WIT export type.
    /// </summary>
    /// <param name="domainItem">The optional domain item to map.</param>
    /// <returns>The mapped WIT export value when present; otherwise, <see langword="null"/>.</returns>
    public TWitExport? MapNullableToWit(TDomain? domainItem)
    {
        return domainItem == null ? null : MapToWit(domainItem);
    }

    /// <summary>
    /// Map nullable WIT export type to domain item.
    /// </summary>
    /// <param name="witItem">The optional WIT export value to map.</param>
    /// <returns>The mapped domain value when present; otherwise, <see langword="null"/>.</returns>
    public TDomain? MapNullableFromWit(TWitExport? witItem)
    {
        return witItem == null ? null : MapFromWit(witItem);
    }
}
