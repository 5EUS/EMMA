using System.Reflection;
using System.Text;

using EMMA.Domain;

namespace EMMA.Cli.Commands;

/// <summary>Scaffolds a Rust plugin from the template compiled into this tool, together with the contract it builds against.</summary>
internal static class NewCommand
{
    // The template is a real, buildable crate; these are the literal values in it that scaffolding replaces.
    private const string TemplateCrate = "emma_plugin_template";
    private const string TemplateId = "com.example.template";
    private const string TemplateName = "Template Plugin";

    private const string TemplatePrefix = "template/";
    private const string ContractPrefix = "wit/";

    /// <summary>Writes a new plugin project.</summary>
    /// <param name="context">The command.</param>
    /// <param name="directory">The directory to create; it must not exist or must be empty.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(CommandContext context, string directory)
    {
        string? id = context.Arguments.Option("id");
        if (!PluginId.TryCreate(id, out PluginId plugin))
        {
            return await context.MisuseAsync("--id is required and must look like com.example.myplugin (lower case, dot separated).").ConfigureAwait(false);
        }

        string name = context.Arguments.Option("name") ?? plugin.Value.Split('.')[^1];
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return await context.FailAsync($"{directory} already exists and is not empty.").ConfigureAwait(false);
        }

        string crate = string.Join('_', plugin.Value.Split('.')).Replace('-', '_');
        Assembly assembly = typeof(NewCommand).Assembly;
        int written = 0;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            bool isTemplate = resource.StartsWith(TemplatePrefix, StringComparison.Ordinal);
            if (!isTemplate && !resource.StartsWith(ContractPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = isTemplate ? resource[TemplatePrefix.Length..] : resource;
            string? text = await ReadAsync(assembly, resource, context.CancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                continue;
            }

            if (isTemplate)
            {
                text = text.Replace(TemplateCrate, crate, StringComparison.Ordinal).Replace(TemplateId, plugin.Value, StringComparison.Ordinal).Replace(TemplateName, name, StringComparison.Ordinal);
            }

            string target = Path.Combine(directory, relative.Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await File.WriteAllTextAsync(target, text, context.CancellationToken).ConfigureAwait(false);
            written++;
        }

        await context.SayAsync($"Created {plugin} in {directory} ({written} files). Next:").ConfigureAwait(false);
        await context.SayAsync($"  cd {directory} && sh build.sh --no-pack && emma plugin run . describe").ConfigureAwait(false);
        await context.SayAsync("  sh build.sh   (builds and packs it into dist/)").ConfigureAwait(false);
        return CommandContext.Success;
    }

    private static async Task<string?> ReadAsync(Assembly assembly, string resource, CancellationToken cancellationToken)
    {
        Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            using StreamReader reader = new(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
