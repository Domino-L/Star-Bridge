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

public sealed record HangarShipCandidate(
    string Title,
    string ManufacturerCode);

public static partial class HangarShipImporter
{
    private static readonly IReadOnlyDictionary<string, string> OfficialNameAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["A1 Spirit"] = "CRUS_A1_Spirit",
            ["Ares Ion"] = "CRUS_Starfighter_Ion",
            ["Asgard"] = "ANVL_Asgard",
            ["Cyclone MT"] = "TMBL_Cyclone_MT",
            ["Dragonfly Black"] = "DRAK_Dragonfly_Black",
            ["Eclipse"] = "AEGS_Eclipse",
            ["F7C-M Super Hornet Mk II"] = "ANVL_Hornet_F7CM_Mk2",
            ["F8C Lightning"] = "ANVL_Lightning_F8C",
            ["Fury MX"] = "Misc_Fury_Miru",
            ["Gladius"] = "AEGS_Gladius",
            ["Hammerhead"] = "AEGS_Hammerhead",
            ["Hercules Starlifter A2"] = "CRUS_Starlifter_A2",
            ["Hurricane"] = "ANVL_Hurricane",
            ["Idris-P Frigate"] = "AEGS_Idris_P",
            ["Ironclad"] = "DRAK_Ironclad",
            ["Ironclad Assault"] = "DRAK_Ironclad_Assault",
            ["M80"] = "ORIG_M80",
            ["Nox"] = "XIAN_Nox",
            ["Origin M80"] = "ORIG_M80",
            ["Railen"] = "XIAN_Railen",
            ["Retaliator"] = "AEGS_Retaliator",
            ["Sabre Firebird"] = "AEGS_Sabre_Firebird",
            ["Scorpius Antares"] = "RSI_Scorpius_Antares",
            ["Starlancer TAC"] = "MISC_Starlancer_TAC",
            ["起源 M80"] = "ORIG_M80",
            ["Starfarer Gemini"] = "MISC_Starfarer_Gemini",
            ["Terrapin"] = "ANVL_Terrapin",
            ["Vanguard Harbinger"] = "AEGS_Vanguard_Harbinger"
        };

    public static HangarImportResult ImportOfficialHangarSnapshot(string content, string language)
    {
        var decoded = WebUtility.HtmlDecode(content);
        var normalizedContent = WhitespaceRegex().Replace(decoded, " ");
        return ImportOfficialShipCandidates(ExtractShipItemCandidates(normalizedContent), language);
    }

    public static HangarImportResult ImportOfficialShipTitles(IEnumerable<string> titles, string language)
    {
        return ImportOfficialShipCandidates(
            titles.Select(title => new HangarShipCandidate(title, "")),
            language);
    }

    public static HangarImportResult ImportOfficialShipCandidates(IEnumerable<HangarShipCandidate> candidates, string language)
    {
        var found = new Dictionary<string, OwnedShipRecord>(StringComparer.OrdinalIgnoreCase);
        var matchedTitles = 0;
        var matchedAliases = 0;

        foreach (var candidate in candidates)
        {
            matchedTitles++;
            if (!TryResolveOfficialShipCandidate(candidate, out var code))
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

    private static IEnumerable<HangarShipCandidate> ExtractShipItemCandidates(string content)
    {
        foreach (Match match in ShipKindRegex().Matches(content))
        {
            var prefixStart = Math.Max(0, match.Index - 1100);
            var prefix = content[prefixStart..match.Index];
            var titleMatches = TitleRegex().Matches(prefix);
            if (titleMatches.Count == 0)
            {
                continue;
            }

            var title = StripTags(titleMatches[^1].Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(title))
            {
                var suffixEnd = Math.Min(content.Length, match.Index + 1200);
                var suffix = content[match.Index..suffixEnd];
                var linerMatch = LinerRegex().Match(suffix);
                var liner = linerMatch.Success
                    ? StripTags(linerMatch.Groups["liner"].Value)
                    : "";

                yield return new HangarShipCandidate(title, ExtractManufacturerCode(liner));
            }
        }
    }

    private static bool TryResolveOfficialShipCandidate(HangarShipCandidate candidate, out string code)
    {
        var title = NormalizeTitle(candidate.Title);
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

        if (TryResolveFromKnownCodes(title, candidate.ManufacturerCode, out code))
        {
            return true;
        }

        code = "";
        return false;
    }

    private static bool TryResolveFromKnownCodes(string title, string manufacturerCode, out string code)
    {
        var titleTokens = MakeSearchTokens(title);
        if (titleTokens.Count == 0)
        {
            code = "";
            return false;
        }

        var titleKey = MakeSearchKey(title);
        var bestCode = "";
        var bestScore = int.MinValue;

        foreach (var knownCode in ShipNameLocalizer.KnownChineseNames.Keys)
        {
            var normalizedCode = ShipNameLocalizer.NormalizeCode(knownCode);
            if (normalizedCode.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                !IsManufacturerCompatible(normalizedCode, manufacturerCode))
            {
                continue;
            }

            var codeWithoutManufacturer = RemoveManufacturerPrefix(normalizedCode);
            var codeTokens = MakeSearchTokens(codeWithoutManufacturer);
            if (codeTokens.Count == 0 || !codeTokens.All(titleTokens.Contains))
            {
                continue;
            }

            var codeKey = MakeSearchKey(codeWithoutManufacturer);
            var score = codeTokens.Count * 10;
            if (titleKey.Equals(codeKey, StringComparison.OrdinalIgnoreCase))
            {
                score += 1000;
            }
            else if (titleKey.Contains(codeKey, StringComparison.OrdinalIgnoreCase) ||
                     codeKey.Contains(titleKey, StringComparison.OrdinalIgnoreCase))
            {
                score += 300;
            }

            if (HasExactManufacturerPrefix(normalizedCode, manufacturerCode))
            {
                score += 50;
            }

            score -= normalizedCode.Length;
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestCode = normalizedCode;
        }

        code = bestCode;
        return !string.IsNullOrWhiteSpace(code);
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

    private static string ExtractManufacturerCode(string value)
    {
        var match = ManufacturerCodeRegex().Match(value);
        return match.Success ? match.Groups["code"].Value.Trim().ToUpperInvariant() : "";
    }

    private static bool IsManufacturerCompatible(string shipCode, string manufacturerCode)
    {
        if (string.IsNullOrWhiteSpace(manufacturerCode))
        {
            return true;
        }

        return HasExactManufacturerPrefix(shipCode, manufacturerCode) ||
               manufacturerCode.Equals("MRAI", StringComparison.OrdinalIgnoreCase) &&
               shipCode.StartsWith("Misc_", StringComparison.OrdinalIgnoreCase) ||
               manufacturerCode.Equals("GAMA", StringComparison.OrdinalIgnoreCase) &&
               shipCode.StartsWith("XIAN_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExactManufacturerPrefix(string shipCode, string manufacturerCode)
    {
        if (string.IsNullOrWhiteSpace(manufacturerCode))
        {
            return false;
        }

        return shipCode.StartsWith($"{manufacturerCode}_", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveManufacturerPrefix(string shipCode)
    {
        var separatorIndex = shipCode.IndexOf('_');
        return separatorIndex > 0 && separatorIndex < shipCode.Length - 1
            ? shipCode[(separatorIndex + 1)..]
            : shipCode;
    }

    private static HashSet<string> MakeSearchTokens(string value)
    {
        return SearchTokenSeparatorRegex()
            .Split(NormalizeSearchText(value))
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string MakeSearchKey(string value)
    {
        return SearchKeySeparatorRegex().Replace(NormalizeSearchText(value), "");
    }

    private static string NormalizeSearchText(string value)
    {
        var normalized = NormalizeTitle(value).ToLowerInvariant();
        normalized = MarkTwoRegex().Replace(normalized, "mk2");
        normalized = ShipVariantHyphenRegex().Replace(normalized, "${left}${right}");
        return normalized;
    }

    [GeneratedRegex(@"<div\s+class=""kind""[^>]*>\s*(Ship|飞船)\s*</div>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShipKindRegex();

    [GeneratedRegex(@"<div\s+class=""title""[^>]*>\s*(?<title>.*?)\s*</div>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<div\s+class=""liner""[^>]*>\s*(?<liner>.*?)\s*</div>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinerRegex();

    [GeneratedRegex(@"\((?<code>[A-Z0-9]{3,5})\)", RegexOptions.CultureInvariant)]
    private static partial Regex ManufacturerCodeRegex();

    [GeneratedRegex(@"\b(?:mark|mk)\s*ii\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkTwoRegex();

    [GeneratedRegex(@"(?<left>[a-z]+\d+[a-z]?)-(?<right>[a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShipVariantHyphenRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchTokenSeparatorRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchKeySeparatorRegex();

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
