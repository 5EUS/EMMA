namespace EMMA.PluginHost.Manifest;

/// <summary>Collects problems found while reading untrusted JSON; only the first is reported, by field path.</summary>
internal sealed class ParseErrors
{
    /// <summary>Gets the first problem, or <see langword="null"/> when none was found.</summary>
    public string? First { get; private set; }

    /// <summary>Records a problem. Later problems are ignored.</summary>
    /// <param name="path">The field path, such as <c>$.entrypoint.path</c>.</param>
    /// <param name="message">What is wrong.</param>
    public void Add(string path, string message) => this.First ??= $"{path}: {message}";
}
