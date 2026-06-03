using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace StarBridge.Desktop;

public sealed record OwnedShipRecord(
    string Code,
    string DisplayName,
    string Source,
    DateTimeOffset ImportedAt);

public sealed record HangarImportResult(
    IReadOnlyList<OwnedShipRecord> Ships,
    int MatchedCodes,
    int MatchedNames);

public static partial class HangarShipImporter
{
    private static readonly IReadOnlyDictionary<string, string> OfficialNameAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dragonfly Black"] = "DRAK_Dragonfly_Black",
            ["F8C Lightning"] = "ANVL_Lightning_F8C",
            ["Idris-P Frigate"] = "AEGS_Idris_P",
            ["Ironclad Assault"] = "DRAK_Ironclad_Assault",
            ["Nox"] = "XIAN_Nox",
            ["Starfarer Gemini"] = "MISC_Starfarer_Gemini"
        };

    public static HangarImportResult ImportOfficialHangarSnapshot(string content, string language)
    {
        var decoded = WebUtility.HtmlDecode(content);
        var normalizedContent = WhitespaceRegex().Replace(decoded, " ");
        return ImportOfficialShipTitles(ExtractShipItemTitles(normalizedContent), language);
    }

    public static HangarImportResult ImportOfficialShipTitles(IEnumerable<string> titles, string language)
    {
        var found = new Dictionary<string, OwnedShipRecord>(StringComparer.OrdinalIgnoreCase);
        var matchedTitles = 0;
        var matchedAliases = 0;

        foreach (var title in titles)
        {
            matchedTitles++;
            if (!TryResolveOfficialShipTitle(title, out var code))
            {
                continue;
            }

            matchedAliases++;
            AddShip(found, code, language);
        }

        return new HangarImportResult(
            found.Values.OrderBy(ship => ship.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            matchedTitles,
            matchedAliases);
    }

    private static IEnumerable<string> ExtractShipItemTitles(string content)
    {
        foreach (Match match in ShipKindRegex().Matches(content))
        {
            var prefixStart = Math.Max(0, match.Index - 900);
            var prefix = content[prefixStart..match.Index];
            var titleMatches = TitleRegex().Matches(prefix);
            if (titleMatches.Count == 0)
            {
                continue;
            }

            var title = StripTags(titleMatches[^1].Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title))
            {
                yield return title;
            }
        }
    }

    private static bool TryResolveOfficialShipTitle(string title, out string code)
    {
        title = NormalizeTitle(title);
        if (OfficialNameAliases.TryGetValue(title, out code!))
        {
            return true;
        }

        foreach (var alias in OfficialNameAliases)
        {
            if (title.Contains(alias.Key, StringComparison.OrdinalIgnoreCase))
            {
                code = alias.Value;
                return true;
            }
        }

        code = "";
        return false;
    }

    private static void AddShip(Dictionary<string, OwnedShipRecord> found, string code, string language)
    {
        code = ShipNameLocalizer.NormalizeCode(code);
        if (found.ContainsKey(code))
        {
            return;
        }

        found[code] = new OwnedShipRecord(
            code,
            ShipNameLocalizer.DisplayName(code, language),
            "RSI 官网机库导入",
            DateTimeOffset.Now);
    }

    private static string NormalizeTitle(string value)
    {
        return WhitespaceRegex()
            .Replace(value.Replace('‘', '\'').Replace('’', '\''), " ")
            .Trim();
    }

    private static string StripTags(string value)
    {
        return NormalizeTitle(TagRegex().Replace(value, ""));
    }

    [GeneratedRegex(@"<div class=""kind"">\s*Ship\s*</div>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShipKindRegex();

    [GeneratedRegex(@"<div class=""title"">\s*(?<title>.*?)\s*</div>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<.*?>", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

public static class ShipDatabaseStore
{
    public static ObservableCollection<OwnedShipRecord> Load(string ownerKey)
    {
        var ships = new ObservableCollection<OwnedShipRecord>();
        var path = GetPath(ownerKey);
        if (!File.Exists(path))
        {
            return ships;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                string.IsNullOrWhiteSpace(parts[1]))
            {
                continue;
            }

            ships.Add(new OwnedShipRecord(
                parts[0],
                parts[1],
                parts[2],
                DateTimeOffset.TryParse(parts[3], out var importedAt)
                    ? importedAt
                    : DateTimeOffset.MinValue));
        }

        return ships;
    }

    public static void Save(string ownerKey, IEnumerable<OwnedShipRecord> ships)
    {
        Directory.CreateDirectory(DesktopAppConfig.ConfigDirectory);
        File.WriteAllLines(
            GetPath(ownerKey),
            ships.Select(ship => string.Join(
                '\t',
                Clean(ship.Code),
                Clean(ship.DisplayName),
                Clean(ship.Source),
                ship.ImportedAt.ToString("O"))),
            Encoding.UTF8);
    }

    private static string GetPath(string ownerKey)
    {
        return Path.Combine(DesktopAppConfig.ConfigDirectory, $"ships-{Sanitize(ownerKey)}.database");
    }

    private static string Sanitize(string value)
    {
        var sanitized = new string(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "local" : sanitized;
    }

    private static string Clean(string value)
    {
        return value.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
