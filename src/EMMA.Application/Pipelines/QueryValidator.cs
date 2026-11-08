using System.Globalization;
using System.Text;

using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Checks a client's request against the plugin's own declaration before any call is
/// made, so a typo in a filter id or an out-of-range value fails fast with a clear
/// message instead of reaching the plugin.
/// </summary>
internal static class QueryValidator
{
    /// <summary>Validates a search query.</summary>
    /// <param name="info">The plugin's declaration.</param>
    /// <param name="query">The client's query.</param>
    /// <returns>The first problem found, or <see langword="null"/> when the query is valid.</returns>
    public static PluginError? ValidateQuery(PluginInfo info, Query query)
    {
        if (query.Text.Length > ContractLimits.MaxQueryTextLength)
        {
            return Invalid($"Search text is longer than {ContractLimits.MaxQueryTextLength} characters.");
        }

        PluginError? paging = ValidatePaging(query.Cursor, query.Limit);
        if (paging is not null)
        {
            return paging;
        }

        NodeKind[] unknownKinds = [.. query.Kinds.Where(kind => info.FindKind(kind) is null)];
        if (unknownKinds.Length > 0)
        {
            return Invalid($"The plugin has no kind '{unknownKinds[0]}'.");
        }

        if (query.Sort is not null && !info.Sorts.Any(sort => string.Equals(sort.Value, query.Sort, StringComparison.Ordinal)))
        {
            return Invalid($"The plugin has no sort order '{query.Sort}'.");
        }

        return ValidateFilters(info, query);
    }

    /// <summary>Validates cursor and limit.</summary>
    /// <param name="cursor">The cursor, if any.</param>
    /// <param name="limit">The limit, if any.</param>
    /// <returns>The first problem found, or <see langword="null"/> when valid.</returns>
    public static PluginError? ValidatePaging(string? cursor, int? limit)
    {
        if (cursor is not null
            && (cursor.Length == 0 || Encoding.UTF8.GetByteCount(cursor) > ContractLimits.MaxCursorBytes))
        {
            return Invalid("The cursor is empty or too long.");
        }

        if (limit is { } value && (value < 1 || value > ContractLimits.MaxNodesPerPage))
        {
            return Invalid($"The limit must be between 1 and {ContractLimits.MaxNodesPerPage}.");
        }

        return null;
    }

    private static PluginError? ValidateFilters(PluginInfo info, Query query)
    {
        HashSet<string> supplied = new(StringComparer.Ordinal);
        foreach (FilterValue value in query.Filters)
        {
            FilterSpec? spec = info.Filters.FirstOrDefault(filter => string.Equals(filter.Id, value.Id, StringComparison.Ordinal));
            if (spec is null)
            {
                return Invalid($"The plugin has no filter '{value.Id}'.");
            }

            if (!supplied.Add(value.Id))
            {
                return Invalid($"The filter '{value.Id}' was supplied twice.");
            }

            PluginError? problem = ValidateFilterValue(spec, value);
            if (problem is not null)
            {
                return problem;
            }
        }

        foreach (FilterSpec spec in info.Filters)
        {
            if (spec.Required && AppliesTo(spec, query) && !supplied.Contains(spec.Id))
            {
                return Invalid($"The filter '{spec.Id}' is required.");
            }
        }

        return null;
    }

    private static bool AppliesTo(FilterSpec spec, Query query) =>
        spec.Kinds.Count == 0 || query.Kinds.Count == 0 || query.Kinds.Intersect(spec.Kinds).Any();

    private static PluginError? ValidateFilterValue(FilterSpec spec, FilterValue value)
    {
        IReadOnlyList<string> values = value.Values;
        if (values.Any(item => item.Length > ContractLimits.MaxQueryTextLength))
        {
            return Invalid($"A value of filter '{spec.Id}' is too long.");
        }

        return spec.Type switch
        {
            FilterType.Toggle => values.Count <= 1
                                ? null
                                : Invalid($"The toggle '{spec.Id}' takes at most one value."),
            FilterType.SingleChoice => values.Count == 1 && IsChoice(spec, values[0])
                                ? null
                                : Invalid($"The filter '{spec.Id}' needs exactly one of its choices."),
            FilterType.MultiChoice => values.All(item => IsChoice(spec, item))
                                ? null
                                : Invalid($"The filter '{spec.Id}' got a value that is not one of its choices."),
            FilterType.Text or FilterType.Lookup => values.Count == 1
                                ? null
                                : Invalid($"The filter '{spec.Id}' takes exactly one value."),
            FilterType.NumberRange => ValidateRange(spec, values),
            _ => Invalid($"The filter '{spec.Id}' has an unknown type."),
        };
    }

    private static bool IsChoice(FilterSpec spec, string value) =>
        spec.Choices.Count == 0 || spec.Choices.Any(choice => string.Equals(choice.Value, value, StringComparison.Ordinal));

    private static PluginError? ValidateRange(FilterSpec spec, IReadOnlyList<string> values)
    {
        if (values.Count != 2)
        {
            return Invalid($"The range '{spec.Id}' needs a minimum and a maximum (either may be empty).");
        }

        double? min = ParseBound(values[0], out bool minValid);
        double? max = ParseBound(values[1], out bool maxValid);
        if (!minValid || !maxValid)
        {
            return Invalid($"The range '{spec.Id}' has a bound that is not a number.");
        }

        return min is { } low && max is { } high && low > high
            ? Invalid($"The range '{spec.Id}' has a minimum above its maximum.")
            : null;
    }

    private static double? ParseBound(string text, out bool valid)
    {
        if (text.Length == 0)
        {
            valid = true;
            return null;
        }

        valid = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed);
        return valid ? parsed : null;
    }

    private static PluginError Invalid(string message) => new(ErrorCode.InvalidArgument, message);
}
