using EMMA.Domain;

namespace EMMA.Storage;

/// <summary>
/// The stored shape of one <see cref="Prop"/>: only the payload that matches
/// <paramref name="Kind"/> is present. Kept typed so stored data never degrades into an untyped bag.
/// </summary>
/// <param name="Key">The prop key.</param>
/// <param name="Kind">Which payload is meaningful.</param>
/// <param name="Text">The text for text and URL values.</param>
/// <param name="Whole">The value for whole-number, flag and timestamp values.</param>
/// <param name="Number">The value for number values.</param>
/// <param name="List">The items for text-list values.</param>
internal sealed record StoredProp(
    string Key,
    PropValueKind Kind,
    string? Text,
    long? Whole,
    double? Number,
    string[]? List);
