using Xunit;

namespace EMMA.Runtime.Tests;

/// <summary>Tests for <see cref="SystemCertificates"/>: Android gets its CA directory, every other platform is left alone.</summary>
public sealed class SystemCertificatesTests
{
    private const string Apex = "/apex/com.android.conscrypt/cacerts";
    private const string System = "/system/etc/security/cacerts";

    /// <summary>A platform without Android's directories, such as desktop Linux, is not touched.</summary>
    [Fact]
    public void LeavesOtherPlatformsAlone()
    {
        Dictionary<string, string> environment = [];

        string? chosen = Run(environment, existing: []);

        Assert.Null(chosen);
        Assert.Empty(environment);
    }

    /// <summary>Android 14 and later keep the updatable store in the conscrypt APEX, and it is preferred.</summary>
    [Fact]
    public void PrefersTheApexStore()
    {
        Dictionary<string, string> environment = [];

        string? chosen = Run(environment, existing: [Apex, System]);

        Assert.Equal(Apex, chosen);
        Assert.Equal(Apex, environment[SystemCertificates.DirectoryVariable]);
    }

    /// <summary>Older Android releases only have the system directory.</summary>
    [Fact]
    public void FallsBackToTheSystemStore()
    {
        Dictionary<string, string> environment = [];

        string? chosen = Run(environment, existing: [System]);

        Assert.Equal(System, chosen);
    }

    /// <summary>A directory or bundle the host or user chose is never overridden.</summary>
    /// <param name="variable">The variable already set.</param>
    [Theory]
    [InlineData(SystemCertificates.DirectoryVariable)]
    [InlineData(SystemCertificates.FileVariable)]
    public void RespectsAnExistingChoice(string variable)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal) { [variable] = "/custom" };

        string? chosen = Run(environment, existing: [Apex, System]);

        Assert.Null(chosen);
        Assert.Equal("/custom", environment[variable]);
        Assert.Single(environment);
    }

    private static string? Run(Dictionary<string, string> environment, string[] existing) =>
        SystemCertificates.Configure(
            name => environment.GetValueOrDefault(name),
            path => existing.Contains(path, StringComparer.Ordinal),
            (name, value) => environment[name] = value);
}
