namespace EMMA.TestSupport;

/// <summary>Finds the built plugin components and fails loudly when they are missing.</summary>
public static class NativeArtifacts
{
    /// <summary>Gets the path of a built fixture plugin.</summary>
    /// <param name="name">The file name without extension, such as <c>fixture</c>.</param>
    /// <returns>The component path.</returns>
    public static string Plugin(string name)
    {
        string path = Path.Combine(RepositoryRoot(), "artifacts", "native", "plugins", name + ".wasm");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"{path} is missing. Run: sh native/build-plugins.sh", path);
    }

    /// <summary>Finds the repository root by walking up from the test output to <c>EMMA.slnx</c>.</summary>
    /// <returns>The root directory.</returns>
    public static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EMMA.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root (EMMA.slnx) was not found above the test output.");
    }
}
