namespace EMMA.Runtime;

/// <summary>The trust section of a configuration document.</summary>
/// <param name="RequireSigned">Whether unsigned plugins are refused; defaults to <see langword="true"/>.</param>
/// <param name="Repositories">The trusted repositories.</param>
internal sealed record TrustDocument(bool? RequireSigned, List<RepositoryDocument>? Repositories);
