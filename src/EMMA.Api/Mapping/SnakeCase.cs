using System.Text;

namespace EMMA.Api.Mapping;

/// <summary>Writes enum member names the way the API spells them: <c>MultiChoice</c> becomes <c>multi_choice</c>.</summary>
internal static class SnakeCase
{
    /// <summary>Converts an enum value to its API spelling.</summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The lower-case snake_case name.</returns>
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string name = value.ToString();
        StringBuilder builder = new(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[index]));
        }

        return builder.ToString();
    }
}
