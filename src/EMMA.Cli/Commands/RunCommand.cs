using System.Globalization;

using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.PluginHost.Signing;
using EMMA.Runtime;

using Microsoft.Data.Sqlite;

namespace EMMA.Cli.Commands;

/// <summary>
/// Tries a plugin under development. It packs the directory (or takes a package), installs it unsigned into a
/// throwaway data directory and calls it through the same pipelines the host uses. Everything the host would refuse
/// (an undeclared kind, a node id that is too long, an asset outside the network permissions) is reported here.
/// </summary>
internal static class RunCommand
{
    private const string Repository = "dev";
    private const string OperationHelp = "Operations: describe | search [text] | children [node] | get <node>... | assets <node> <role> | content <node> <role> [--index n] [--out file]. Add --settings <file.json> to give the plugin values for its settings.";

    /// <summary>Runs one operation against a plugin.</summary>
    /// <param name="context">The command.</param>
    /// <param name="source">A plugin directory or a package file.</param>
    /// <param name="operation">The operation: <c>describe</c>, <c>search</c>, <c>children</c>, <c>get</c>, <c>assets</c> or <c>content</c>.</param>
    /// <param name="operands">The operation's arguments.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(CommandContext context, string source, string operation, IReadOnlyList<string> operands)
    {
        Outcome<byte[]> package = await PackageAsync(source, context.CancellationToken).ConfigureAwait(false);
        if (!package.IsSuccess)
        {
            return await context.FailAsync(package.Error).ConfigureAwait(false);
        }

        DirectoryInfo scratch = Directory.CreateTempSubdirectory("emma-run-");
        try
        {
            EmmaRuntime runtime = new(new EmmaRuntimeOptions(scratch.FullName) { Trust = TrustPolicy.Create([], requireSigned: false) });
            await using (runtime.ConfigureAwait(false))
            {
                await runtime.InitializeAsync(context.CancellationToken).ConfigureAwait(false);
                using MemoryStream stream = new(package.Value);
                Outcome<InstalledPlugin> installed = await runtime.InstallAsync(stream, Repository, context.CancellationToken).ConfigureAwait(false);
                if (!installed.IsSuccess)
                {
                    return await context.FailAsync(installed.Error).ConfigureAwait(false);
                }

                PluginError? unusable = await ApplySettingsAsync(context, runtime, installed.Value.Id).ConfigureAwait(false);
                return unusable is null
                    ? await ExecuteAsync(context, runtime, installed.Value.Id, operation, operands).ConfigureAwait(false)
                    : await context.FailAsync(unusable).ConfigureAwait(false);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            scratch.Delete(recursive: true);
        }
    }

    /// <summary>Gives the plugin the values from <c>--settings</c>, if given, checked against what the plugin declares like any other change.</summary>
    private static async Task<PluginError?> ApplySettingsAsync(CommandContext context, EmmaRuntime runtime, PluginId plugin)
    {
        if (context.Arguments.Option("settings") is not { } path)
        {
            return null;
        }

        Outcome<IReadOnlyList<SettingChange>> changes = await SettingsFile.ReadAsync(path, context.CancellationToken).ConfigureAwait(false);
        if (!changes.IsSuccess)
        {
            return changes.Error;
        }

        Outcome<IReadOnlyList<SettingState>> applied = await runtime.SetPluginSettingsAsync(plugin, changes.Value, context.CancellationToken).ConfigureAwait(false);
        return applied.IsSuccess ? null : applied.Error;
    }

    private static async Task<Outcome<byte[]>> PackageAsync(string source, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            return Outcome.Success(await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false));
        }

        Outcome<PluginPackage> built = await PackCommand.BuildAsync(source, signing: null, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        return built.IsSuccess ? Outcome.Success(built.Value.Archive) : Outcome.Failure<byte[]>(built.Error);
    }

    private static Task<int> ExecuteAsync(CommandContext context, EmmaRuntime runtime, PluginId plugin, string operation, IReadOnlyList<string> operands) => operation switch
    {
        "describe" => DescribeAsync(context, runtime, plugin),
        "search" => SearchAsync(context, runtime, plugin, operands),
        "children" => ChildrenAsync(context, runtime, plugin, operands),
        "get" when operands.Count > 0 => GetAsync(context, runtime, plugin, operands),
        "assets" or "content" when operands.Count >= 2 => AssetsAsync(context, runtime, plugin, operation, operands),
        _ => context.MisuseAsync(OperationHelp),
    };

    private static async Task<int> DescribeAsync(CommandContext context, EmmaRuntime runtime, PluginId plugin)
    {
        Outcome<PluginDetail> detail = await runtime.DescribePluginAsync(plugin, context.CancellationToken).ConfigureAwait(false);
        if (!detail.IsSuccess)
        {
            return await context.FailAsync(detail.Error).ConfigureAwait(false);
        }

        await PluginCommands.PrintDetailAsync(context, detail.Value).ConfigureAwait(false);
        return CommandContext.Success;
    }

    private static async Task<int> SearchAsync(CommandContext context, EmmaRuntime runtime, PluginId plugin, IReadOnlyList<string> operands)
    {
        Outcome<PageRequest> page = ReadPage(context);
        if (!page.IsSuccess)
        {
            return await context.MisuseAsync(page.Error.Message).ConfigureAwait(false);
        }

        Query query = new(operands.Count > 0 ? operands[0] : string.Empty, [], [], null, page.Value.Cursor, page.Value.Limit);
        return await PrintAsync(context, await runtime.Catalog.SearchAsync(plugin, query, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task<int> ChildrenAsync(CommandContext context, EmmaRuntime runtime, PluginId plugin, IReadOnlyList<string> operands)
    {
        Outcome<PageRequest> page = ReadPage(context);
        if (!page.IsSuccess)
        {
            return await context.MisuseAsync(page.Error.Message).ConfigureAwait(false);
        }

        if (operands.Count == 0)
        {
            return await PrintAsync(context, await runtime.Catalog.BrowseRootAsync(plugin, page.Value, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }

        Outcome<NodeRef> parent = Node(plugin, operands[0]);
        return parent.IsSuccess
            ? await PrintAsync(context, await runtime.Catalog.ChildrenAsync(parent.Value, page.Value, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false)
            : await context.MisuseAsync(parent.Error.Message).ConfigureAwait(false);
    }

    private static async Task<int> GetAsync(CommandContext context, EmmaRuntime runtime, PluginId plugin, IReadOnlyList<string> operands)
    {
        List<NodeRef> references = [];
        foreach (string operand in operands)
        {
            Outcome<NodeRef> node = Node(plugin, operand);
            if (!node.IsSuccess)
            {
                return await context.MisuseAsync(node.Error.Message).ConfigureAwait(false);
            }

            references.Add(node.Value);
        }

        Outcome<IReadOnlyList<Node>> nodes = await runtime.Catalog.GetAsync(references, context.CancellationToken).ConfigureAwait(false);
        if (!nodes.IsSuccess)
        {
            return await context.FailAsync(nodes.Error).ConfigureAwait(false);
        }

        await PluginCommands.PrintAsync(context, new NodeListResponse([.. nodes.Value.Select(NodeMapper.ToDto)]), CliJsonContext.Default.NodeListResponse).ConfigureAwait(false);
        return CommandContext.Success;
    }

    private static async Task<int> AssetsAsync(CommandContext context, EmmaRuntime runtime, PluginId plugin, string operation, IReadOnlyList<string> operands)
    {
        Outcome<NodeRef> node = Node(plugin, operands[0]);
        Outcome<AssetRole> role = RequestParsing.Role(operands[1]);
        if (!node.IsSuccess || !role.IsSuccess)
        {
            return await context.MisuseAsync((node.Error ?? role.Error)!.Message).ConfigureAwait(false);
        }

        if (string.Equals(operation, "assets", StringComparison.Ordinal))
        {
            Outcome<IReadOnlyList<Asset>> assets = await runtime.Assets.ResolveAsync(node.Value, role.Value, [], context.CancellationToken).ConfigureAwait(false);
            if (!assets.IsSuccess)
            {
                return await context.FailAsync(assets.Error).ConfigureAwait(false);
            }

            await PluginCommands.PrintAsync(context, AssetMapper.ToResponse(assets.Value), CliJsonContext.Default.AssetListResponse).ConfigureAwait(false);
            return CommandContext.Success;
        }

        int index = int.TryParse(context.Arguments.Option("index") ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0 ? parsed : 0;
        Outcome<AssetBytes> bytes = await runtime.ReadAssetAsync(node.Value, role.Value, index, range: null, context.CancellationToken).ConfigureAwait(false);
        if (!bytes.IsSuccess)
        {
            return await context.FailAsync(bytes.Error).ConfigureAwait(false);
        }

        if (context.Arguments.Option("out") is { } path)
        {
            await File.WriteAllBytesAsync(path, bytes.Value.Bytes.ToArray(), context.CancellationToken).ConfigureAwait(false);
            await context.SayAsync($"Wrote {bytes.Value.Bytes.Length} bytes of {bytes.Value.Mime} to {path}.").ConfigureAwait(false);
        }
        else
        {
            await context.SayAsync($"{bytes.Value.Bytes.Length} bytes of {bytes.Value.Mime} (use --out <file> to save them).").ConfigureAwait(false);
        }

        return CommandContext.Success;
    }

    private static async Task<int> PrintAsync(CommandContext context, Outcome<NodePage> page)
    {
        if (!page.IsSuccess)
        {
            return await context.FailAsync(page.Error).ConfigureAwait(false);
        }

        await PluginCommands.PrintAsync(context, NodeMapper.ToDto(page.Value), CliJsonContext.Default.NodePageResponse).ConfigureAwait(false);
        return CommandContext.Success;
    }

    private static Outcome<PageRequest> ReadPage(CommandContext context)
    {
        int? limit = int.TryParse(context.Arguments.Option("limit"), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
        return RequestParsing.Page(context.Arguments.Option("cursor"), limit);
    }

    /// <summary>Accepts either a full reference or just the plugin's own id for the node.</summary>
    private static Outcome<NodeRef> Node(PluginId plugin, string text)
    {
        if (NodeRef.TryParse(text, out NodeRef full) && full.Plugin == plugin)
        {
            return Outcome.Success(full);
        }

        return LocalNodeId.TryCreate(text, out LocalNodeId local)
            ? Outcome.Success(new NodeRef(plugin, local))
            : Outcome.Failure<NodeRef>(ErrorCode.InvalidArgument, $"'{text}' is not a valid node id.");
    }
}
