namespace EMMA.Application.Ports;

/// <summary>
/// Encrypts small secrets for storage, so a copy of the database alone does not reveal them. The text it produces is
/// safe to store and is only meaningful to the box that made it.
/// </summary>
public interface ISecretBox
{
    /// <summary>Encrypts a secret.</summary>
    /// <param name="plaintext">The secret.</param>
    /// <param name="context">What the secret is for, such as the plugin and setting. It is not secret, but the result opens only with the same context, so a stored value cannot be moved to another row.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Text to store.</returns>
    Task<string> ProtectAsync(string plaintext, string context, CancellationToken cancellationToken);

    /// <summary>Decrypts a secret.</summary>
    /// <param name="protectedText">What <see cref="ProtectAsync"/> returned.</param>
    /// <param name="context">The context it was protected with.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The secret, or <see langword="null"/> when the text is damaged, was made for another context, or was made with a key this install no longer has.</returns>
    Task<string?> UnprotectAsync(string protectedText, string context, CancellationToken cancellationToken);
}
