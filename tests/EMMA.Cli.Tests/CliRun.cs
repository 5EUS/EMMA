using System.IO.Compression;
using System.Text.Json.Nodes;

using EMMA.TestSupport;

namespace EMMA.Cli.Tests;

/// <summary>The result of running the command line.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error.</param>
internal sealed record CliRun(int ExitCode, string Output, string Error)
{
    /// <summary>Runs the real command line with output captured.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>What it did.</returns>
    public static async Task<CliRun> ExecuteAsync(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await CliApp.RunAsync(args, output, error, CancellationToken.None).ConfigureAwait(false);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Runs the real command line with standard input supplied and output captured.</summary>
    /// <param name="input">What standard input holds.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>What it did.</returns>
    public static async Task<CliRun> ExecuteWithInputAsync(string input, params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        using StringReader reader = new(input);
        int exitCode = await CliApp.RunAsync(args, output, error, CancellationToken.None, reader).ConfigureAwait(false);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Makes a plugin directory the way an author would have one: the template's manifest and a built component.</summary>
    /// <param name="adjust">Changes the manifest text, if needed.</param>
    /// <param name="component">Which built test plugin to use as the component: <c>template</c>, or <c>fixture</c> (which declares settings).</param>
    /// <returns>The directory.</returns>
    public static async Task<DirectoryInfo> PluginDirectoryAsync(Func<string, string>? adjust = null, string component = "template")
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("emma-cli-plugin-");
        string manifest = await File.ReadAllTextAsync(Path.Combine(NativeArtifacts.RepositoryRoot(), "native", "templates", "rust-plugin", "plugin.json")).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "plugin.json"), adjust?.Invoke(manifest) ?? manifest).ConfigureAwait(false);
        File.Copy(NativeArtifacts.Plugin(component), Path.Combine(directory.FullName, "plugin.wasm"));
        return directory;
    }

    /// <summary>Reads a package's entry names and its manifest.</summary>
    /// <param name="path">The zip file.</param>
    /// <returns>The sorted entry names and the parsed manifest.</returns>
    public static async Task<(string[] Entries, JsonNode Manifest)> InspectAsync(string path)
    {
        ZipArchive archive = await ZipFile.OpenReadAsync(path).ConfigureAwait(false);
        await using (archive.ConfigureAwait(false))
        {
            Stream stream = await archive.GetEntry("plugin.json")!.OpenAsync().ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using StreamReader reader = new(stream);
                JsonNode manifest = JsonNode.Parse(await reader.ReadToEndAsync().ConfigureAwait(false))!;
                return ([.. archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal)], manifest);
            }
        }
    }
}
