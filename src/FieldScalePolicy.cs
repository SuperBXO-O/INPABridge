namespace INPABridge;

public readonly record struct FieldScaleResult(float BoxScale, float FontScale);

public static class FieldScalePolicy
{
    public static FieldScaleResult Compute(float displayWidth, float displayHeight, float globalScale)
    {
        var w = float.IsFinite(displayWidth) ? Math.Max(1f, displayWidth) : 1920f;
        var h = float.IsFinite(displayHeight) ? Math.Max(1f, displayHeight) : 1080f;
        var ui = float.IsFinite(globalScale) ? Math.Clamp(globalScale, 0.75f, 2f) : 1f;

        var areaRatio = MathF.Sqrt((w * h) / (1920f * 1080f));
        var resolutionScale = Math.Clamp(areaRatio * 0.75f, 1f, 2f);
        var boxScale = Math.Clamp(Math.Max(ui, resolutionScale), 1f, 2f);
        var fontScale = Math.Clamp(1f + (boxScale - 1f) * 0.4f, 0.9f, 1.6f);
        return new FieldScaleResult(boxScale, fontScale);
    }
}
