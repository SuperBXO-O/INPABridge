using INPABridge;

var failures = new List<string>();
var variantCases = new[]
{
    new VariantCase("main", "inpa", "INPA", "fixture-inpa"),
    new VariantCase("princeton", "princeton", "普林斯顿", "fixture-princeton"),
    new VariantCase("generic", "generic", "通用版", "fixture-generic"),
};

foreach (var test in variantCases)
{
    var html = $"<html data-fd-variant=\"{test.Variant}\"></html>";
    var source = RoomAlertPolicy.ResolveSource(
        html,
        profile => "fixture-" + profile,
        "fixture-default",
        "旧版");

    Check(test.Variant + " profile", source?.Profile, test.ExpectedProfile);
    Check(test.Variant + " data path", source?.DataPath, test.ExpectedPath);
    Check(test.Variant + " shop label", source?.ShopLabel, test.ExpectedLabel);
}

var tempDir = Path.Combine(Path.GetTempPath(), "inpa-room-alert-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    var statePath = Path.Combine(tempDir, "inpa-data.json");
    const long now = 1_800_000_000_000;
    var startMs = now - 31L * 60L * 1000L;
    File.WriteAllText(
        statePath,
        $$"""
          {
            "rev": 1,
            "data": {
              "roomAlert": { "on": true, "preMin": 5 },
              "roomLog": [
                {
                  "id": 7,
                  "roomNo": "009",
                  "staff": "测试店员",
                  "hours": 0.5,
                  "startMs": {{startMs}},
                  "end": null
                }
              ]
            }
          }
          """);

    var fired = new HashSet<string>();
    var first = RoomAlertPolicy.Scan(statePath, "INPA", now, fired);
    Check("due alert count", first.Count, 1);
    if (first.Count > 0)
    {
        Check("due alert text", first[0].Text, "【INPA 包间】测试店员 的 009 号房 已到时间，请确认续时或结束");
        Check("due audit text", first[0].AuditText, "包间提醒（到点）：测试店员 的 009 号房");
    }

    var second = RoomAlertPolicy.Scan(statePath, "INPA", now, fired);
    Check("same due alert is not repeated", second.Count, 0);
}
finally
{
    Directory.Delete(tempDir, recursive: true);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAIL RoomAlertPolicy");
    foreach (var failure in failures) Console.Error.WriteLine("  " + failure);
    Environment.Exit(1);
}

Console.WriteLine("PASS RoomAlertPolicy");
return;

void Check<T>(string name, T actual, T expected)
{
    if (EqualityComparer<T>.Default.Equals(actual, expected)) return;
    failures.Add($"{name}: expected {expected}, got {actual}");
}

internal sealed record VariantCase(
    string Variant,
    string ExpectedProfile,
    string ExpectedLabel,
    string ExpectedPath);
