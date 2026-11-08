using System.Globalization;

namespace EMMA.PluginHost.Install;

/// <summary>Compares semantic versions (already validated by the manifest parser), so the installer can refuse a downgrade.</summary>
internal static class PluginVersion
{
    /// <summary>Compares two semantic versions by the SemVer precedence rules.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>Negative when <paramref name="left"/> is older, zero when equal, positive when newer.</returns>
    public static int Compare(string left, string right)
    {
        (long[] leftCore, string[] leftPre) = Split(left);
        (long[] rightCore, string[] rightPre) = Split(right);

        for (int index = 0; index < 3; index++)
        {
            int byPart = leftCore[index].CompareTo(rightCore[index]);
            if (byPart != 0)
            {
                return byPart;
            }
        }

        // A release is newer than any pre-release of the same core version.
        if (leftPre.Length == 0 || rightPre.Length == 0)
        {
            return rightPre.Length.CompareTo(leftPre.Length);
        }

        return ComparePrerelease(leftPre, rightPre);
    }

    private static int ComparePrerelease(string[] left, string[] right)
    {
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool leftNumeric = long.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out long leftNumber);
            bool rightNumeric = long.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out long rightNumber);

            // Numeric identifiers sort below alphanumeric ones.
            int result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[index], right[index]),
            };

            if (result != 0)
            {
                return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static (long[] Core, string[] Pre) Split(string version)
    {
        int dash = version.IndexOf('-', StringComparison.Ordinal);
        string core = dash < 0 ? version : version[..dash];
        string[] pre = dash < 0 ? [] : version[(dash + 1)..].Split('.');
        long[] numbers = [.. core.Split('.').Select(part => long.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture))];
        return (numbers, pre);
    }
}
