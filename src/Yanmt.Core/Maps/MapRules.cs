namespace Yanmt.Core.Maps;

/// <summary>Validation and limits shared by the map editor and the map service.</summary>
public static class MapRules
{
    public const int MaxNameLength = 100;
    public const int MaxLinkLabelLength = 64;

    public const int DefaultWidth = 1600;
    public const int DefaultHeight = 1000;
    public const int MinCanvasSize = 400;
    public const int MaxCanvasSize = 10000;

    /// <summary>Largest accepted background image upload.</summary>
    public const long MaxBackgroundBytes = 10 * 1024 * 1024;

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Map name is required.";
        }

        name = name.Trim();
        if (name.Length > MaxNameLength)
        {
            return $"Map name must be at most {MaxNameLength} characters.";
        }

        return name.Any(char.IsControl) ? "Map name can't contain control characters." : null;
    }

    public static string? ValidateLinkLabel(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return null;
        }

        if (label.Length > MaxLinkLabelLength)
        {
            return $"Link label must be at most {MaxLinkLabelLength} characters.";
        }

        return label.Any(char.IsControl) ? "Link label can't contain control characters." : null;
    }

    public static string? ValidateCanvas(int width, int height) =>
        width is < MinCanvasSize or > MaxCanvasSize || height is < MinCanvasSize or > MaxCanvasSize
            ? $"Canvas width and height must be between {MinCanvasSize} and {MaxCanvasSize} pixels."
            : null;

    /// <summary>Keeps an element's centre on the canvas (positions come from the browser).</summary>
    public static (double X, double Y) Clamp(double x, double y, int width, int height)
    {
        static double C(double v, int max) => double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0, max), 1) : max / 2.0;
        return (C(x, width), C(y, height));
    }
}
