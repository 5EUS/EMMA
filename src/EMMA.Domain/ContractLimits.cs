namespace EMMA.Domain;

/// <summary>
/// Size limits the host enforces on everything a plugin returns. See
/// <c>docs/architecture/contract.md</c> section 9.
/// </summary>
public static class ContractLimits
{
    /// <summary>Maximum UTF-8 length of a plugin-local node id.</summary>
    public const int MaxNodeIdBytes = 512;

    /// <summary>Maximum UTF-8 length of a node title.</summary>
    public const int MaxTitleBytes = 1024;

    /// <summary>Maximum number of props on one node.</summary>
    public const int MaxPropsPerNode = 64;

    /// <summary>Maximum length of a prop key, in characters.</summary>
    public const int MaxPropKeyLength = 128;

    /// <summary>Maximum number of nodes in one page.</summary>
    public const int MaxNodesPerPage = 500;

    /// <summary>Maximum UTF-8 length of a paging cursor.</summary>
    public const int MaxCursorBytes = 2048;

    /// <summary>Maximum size of an inline asset payload, in bytes.</summary>
    public const int MaxInlineAssetBytes = 1024 * 1024;

    /// <summary>Maximum length of search text or a filter value, in characters.</summary>
    public const int MaxQueryTextLength = 512;

    /// <summary>Maximum number of node ids in one <c>get</c> call.</summary>
    public const int MaxIdsPerGet = 500;

    /// <summary>Maximum length of a plugin id, in characters.</summary>
    public const int MaxPluginIdLength = 128;

    /// <summary>Maximum length of one value of a user setting, in characters, whatever the setting's own limit.</summary>
    public const int MaxSettingValueLength = 4096;
}
