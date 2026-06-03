using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace StarBridge.Desktop;

public static partial class ShipNameLocalizer
{
    private const string Unknown = "Unknown";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ChineseNames = new(LoadChineseNames);

    public static string DisplayName(string? shipCode, string language)
    {
        var normalized = NormalizeCode(shipCode);
        if (normalized.Equals(Unknown, StringComparison.OrdinalIgnoreCase) ||
            !language.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return ChineseNames.Value.TryGetValue(normalized, out var localized)
            ? localized
            : normalized;
    }

    public static IReadOnlyDictionary<string, string> KnownChineseNames => ChineseNames.Value;

    public static string NormalizeCode(string? shipCode)
    {
        if (string.IsNullOrWhiteSpace(shipCode) ||
            shipCode.Equals(Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return Unknown;
        }

        var normalized = EntityIdSuffixRegex().Replace(shipCode.Trim(), "");
        return string.IsNullOrWhiteSpace(normalized) ? Unknown : normalized;
    }

    private static IReadOnlyDictionary<string, string> LoadChineseNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "ship-names-zh.txt");
        if (!File.Exists(path))
        {
            return names;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
            {
                continue;
            }

            var rawKey = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            var isShortName = rawKey.EndsWith("_short", StringComparison.OrdinalIgnoreCase);
            var code = rawKey;

            if (code.StartsWith("vehicle_Name", StringComparison.OrdinalIgnoreCase))
            {
                code = code["vehicle_Name".Length..];
            }
            else
            {
                continue;
            }

            if (isShortName)
            {
                code = code[..^"_short".Length];
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (isShortName || !names.ContainsKey(code))
            {
                names[code] = value;
            }
        }

        return names;
    }

    [GeneratedRegex(@"_\d+$", RegexOptions.Compiled)]
    private static partial Regex EntityIdSuffixRegex();
}
