using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace INPABridge;

public sealed record RoomAlertSource(string Profile, string DataPath, string ShopLabel);

public sealed record RoomAlertNotice(string Text, string AuditText);

public static partial class RoomAlertPolicy
{
    public static RoomAlertSource? ResolveSource(
        string? pageHtml,
        Func<string, string?> stateFileForProfile,
        string? defaultStatePath,
        string fallbackShopLabel)
    {
        var profile = ProfileFromPageHtml(pageHtml);
        if (profile != null)
        {
            var path = stateFileForProfile(profile);
            if (!string.IsNullOrWhiteSpace(path))
                return new RoomAlertSource(profile, path, ShopLabelForProfile(profile));
        }

        if (string.IsNullOrWhiteSpace(defaultStatePath)) return null;
        return new RoomAlertSource("", defaultStatePath, fallbackShopLabel);
    }

    public static string? ProfileFromPageHtml(string? pageHtml)
    {
        if (string.IsNullOrEmpty(pageHtml)) return null;
        var match = VariantPattern().Match(pageHtml);
        if (!match.Success) return null;

        return match.Groups[1].Value switch
        {
            "main" => "inpa",
            "princeton" => "princeton",
            "generic" => "generic",
            _ => null,
        };
    }

    public static string ShopLabelForProfile(string profile) => profile switch
    {
        "inpa" => "INPA",
        "princeton" => "普林斯顿",
        "generic" => "通用版",
        _ => profile,
    };

    public static IReadOnlyList<RoomAlertNotice> Scan(
        string dataPath,
        string shopLabel,
        long nowMs,
        ISet<string> fired)
    {
        if (string.IsNullOrWhiteSpace(dataPath) || !File.Exists(dataPath)) return Array.Empty<RoomAlertNotice>();

        string text;
        try { text = File.ReadAllText(dataPath, Encoding.UTF8); }
        catch { return Array.Empty<RoomAlertNotice>(); }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch { return Array.Empty<RoomAlertNotice>(); }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Array.Empty<RoomAlertNotice>();

            var on = true;
            var preMin = 5;
            if (data.TryGetProperty("roomAlert", out var alert) && alert.ValueKind == JsonValueKind.Object)
            {
                if (alert.TryGetProperty("on", out var enabled) &&
                    (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False))
                {
                    on = enabled.GetBoolean();
                }

                if (alert.TryGetProperty("preMin", out var preMinutes) && preMinutes.ValueKind == JsonValueKind.Number)
                {
                    try { preMin = preMinutes.GetInt32(); }
                    catch { }
                }
            }

            if (!on) return Array.Empty<RoomAlertNotice>();
            if (!data.TryGetProperty("roomLog", out var log) || log.ValueKind != JsonValueKind.Array)
                return Array.Empty<RoomAlertNotice>();

            var notices = new List<RoomAlertNotice>();
            foreach (var record in log.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object) continue;
                if (record.TryGetProperty("end", out var end) &&
                    end.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(end.GetString()))
                {
                    continue;
                }

                long start = 0;
                if (record.TryGetProperty("startMs", out var startMs) && startMs.ValueKind == JsonValueKind.Number)
                {
                    try { start = startMs.GetInt64(); }
                    catch { }
                }

                if (start <= 0 &&
                    record.TryGetProperty("start", out var startText) &&
                    startText.ValueKind == JsonValueKind.String)
                {
                    start = ParseStartMs(startText.GetString());
                }

                if (start <= 0) continue;

                double hours = 0;
                if (record.TryGetProperty("hours", out var hoursElement) && hoursElement.ValueKind == JsonValueKind.Number)
                {
                    try { hours = hoursElement.GetDouble(); }
                    catch { }
                }

                if (hours <= 0) continue;

                var due = start + (long)(hours * 3600.0 * 1000.0);
                var roomNo = record.TryGetProperty("roomNo", out var roomElement) ? (roomElement.GetString() ?? "") : "";
                var staff = record.TryGetProperty("staff", out var staffElement) ? (staffElement.GetString() ?? "") : "";
                var id = record.TryGetProperty("id", out var idElement) ? idElement.ToString() : "";
                var who = (staff.Length > 0 ? staff + " 的 " : "") + roomNo + " 号房";
                var preAt = due - (long)preMin * 60000L;
                var tag = dataPath + "|" + id;

                if (nowMs >= due)
                {
                    if (fired.Add(tag + "|due"))
                    {
                        notices.Add(new RoomAlertNotice(
                            "【" + shopLabel + " 包间】" + who + " 已到时间，请确认续时或结束",
                            "包间提醒（到点）：" + who));
                    }
                }
                else if (preMin > 0 && nowMs >= preAt)
                {
                    if (fired.Add(tag + "|pre"))
                    {
                        notices.Add(new RoomAlertNotice(
                            "【" + shopLabel + " 包间】" + who + " 还有 " + preMin + " 分钟到点",
                            "包间提醒（提前 " + preMin + " 分钟）：" + who));
                    }
                }
            }

            return notices;
        }
    }

    private static long ParseStartMs(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        var match = StartPattern().Match(value);
        if (!match.Success) return 0;

        try
        {
            var dateTime = new DateTime(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value),
                int.Parse(match.Groups[4].Value),
                int.Parse(match.Groups[5].Value),
                match.Groups[6].Success && match.Groups[6].Value.Length > 0
                    ? int.Parse(match.Groups[6].Value)
                    : 0,
                DateTimeKind.Local);
            return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds();
        }
        catch
        {
            return 0;
        }
    }

    [GeneratedRegex("data-fd-variant\\s*=\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex VariantPattern();

    [GeneratedRegex("(\\d{4})\\D+(\\d{1,2})\\D+(\\d{1,2})\\D+(\\d{1,2}):(\\d{1,2}):?(\\d{1,2})?", RegexOptions.CultureInvariant)]
    private static partial Regex StartPattern();
}
