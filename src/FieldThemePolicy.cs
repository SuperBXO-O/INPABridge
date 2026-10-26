namespace INPABridge;

/// <summary>
/// Keeps the editable field's detected web profile stable while unrelated
/// background requests are being handled.
/// </summary>
public static class FieldThemePolicy
{
    public static string NormalizeProfile(string? profile)
    {
        var value = (profile ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "inpa" => "inpa",
            "princeton" => "princeton",
            "generic" => "generic",
            _ => "",
        };
    }

    public static string UpdateDetectedProfile(
        string? currentProfile,
        string? requestProfile,
        bool editingRequest)
    {
        var current = NormalizeProfile(currentProfile);
        if (!editingRequest) return current;

        var requested = NormalizeProfile(requestProfile);
        return requested.Length > 0 ? requested : current;
    }

    public static int LayoutModeForProfile(string? profile, bool cyberCyan)
    {
        return NormalizeProfile(profile) switch
        {
            "inpa" => 1,
            "princeton" => 2,
            "generic" => cyberCyan ? 4 : 3,
            _ => 0,
        };
    }
}
