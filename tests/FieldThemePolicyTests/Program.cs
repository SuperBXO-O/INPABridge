using INPABridge;

var failures = new List<string>();

var profile = "inpa";
profile = FieldThemePolicy.UpdateDetectedProfile(profile, "generic", false);
Check("unrelated generic request does not switch profile", profile, "inpa");
profile = FieldThemePolicy.UpdateDetectedProfile(profile, "princeton", false);
Check("unrelated princeton request does not switch profile", profile, "inpa");
profile = FieldThemePolicy.UpdateDetectedProfile(profile, "inpa", true);
Check("editing INPA field keeps profile", profile, "inpa");

var mode = FieldThemePolicy.LayoutModeForProfile(profile, false);
Check("INPA field stays black and yellow", mode, 1);

profile = FieldThemePolicy.UpdateDetectedProfile(profile, "generic", true);
Check("editing generic field switches profile", profile, "generic");
mode = FieldThemePolicy.LayoutModeForProfile(profile, false);
Check("generic field uses orange and white", mode, 3);
mode = FieldThemePolicy.LayoutModeForProfile(profile, true);
Check("cyber generic field uses neon cyan", mode, 4);

profile = FieldThemePolicy.UpdateDetectedProfile(profile, "", true);
Check("empty editing request keeps previous profile", profile, "generic");
profile = FieldThemePolicy.UpdateDetectedProfile(profile, "unknown", true);
Check("unknown editing request keeps previous profile", profile, "generic");

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAIL FieldThemePolicy");
    foreach (var failure in failures) Console.Error.WriteLine("  " + failure);
    Environment.Exit(1);
}

Console.WriteLine("PASS FieldThemePolicy");
return;

void Check<T>(string name, T actual, T expected)
{
    if (EqualityComparer<T>.Default.Equals(actual, expected)) return;
    failures.Add($"{name}: expected {expected}, got {actual}");
}
