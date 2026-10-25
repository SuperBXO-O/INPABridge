using INPABridge;

var failures = new List<string>();
var tests = new[]
{
    new ScaleCase("1080p at 100%", 1920f, 1080f, 1f, 1f, 1f),
    new ScaleCase("1440p at 125%", 2560f, 1440f, 1.25f, 1.25f, 1.1f),
    new ScaleCase("4K at 150%", 3840f, 2160f, 1.5f, 1.5f, 1.2f),
    new ScaleCase("4K at 100%", 3840f, 2160f, 1f, 1.5f, 1.2f),
    new ScaleCase("4K at 200%", 3840f, 2160f, 2f, 2f, 1.4f),
    new ScaleCase("Invalid dimensions", 0f, 0f, 1f, 1f, 1f),
};

foreach (var test in tests)
{
    var actual = FieldScalePolicy.Compute(test.Width, test.Height, test.UiScale);
    Check(test.Name + " box scale", actual.BoxScale, test.ExpectedBox);
    Check(test.Name + " font scale", actual.FontScale, test.ExpectedFont);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAIL FieldScalePolicy");
    foreach (var failure in failures) Console.Error.WriteLine("  " + failure);
    Environment.Exit(1);
}

Console.WriteLine($"PASS FieldScalePolicy ({tests.Length * 2} assertions)");
return;

void Check(string name, float actual, float expected)
{
    if (Math.Abs(actual - expected) <= 0.001f) return;
    failures.Add($"{name}: expected {expected:0.###}, got {actual:0.###}");
}

internal sealed record ScaleCase(
    string Name,
    float Width,
    float Height,
    float UiScale,
    float ExpectedBox,
    float ExpectedFont);
