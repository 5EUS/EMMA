using System.Text;

using EMMA.Application.Ports;

namespace EMMA.TestSupport;

/// <summary>
/// A reversible stand-in for an <see cref="ISecretBox"/>, for tests of the code around it. It is not encryption, but like
/// the real thing it never stores the plaintext as it is, ties a value to its context, and cannot open what another
/// key made.
/// </summary>
/// <param name="key">Stands for the encryption key; two boxes with different keys cannot open each other's values.</param>
public sealed class FakeSecretBox(string key = "key-1") : ISecretBox
{
    /// <inheritdoc/>
    public Task<string> ProtectAsync(string plaintext, string context, CancellationToken cancellationToken) =>
        Task.FromResult(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}\n{context}\n{plaintext}")));

    /// <inheritdoc/>
    public Task<string?> UnprotectAsync(string protectedText, string context, CancellationToken cancellationToken)
    {
        string prefix = $"{key}\n{context}\n";
        try
        {
            string text = Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            return Task.FromResult(text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : null);
        }
        catch (FormatException)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
