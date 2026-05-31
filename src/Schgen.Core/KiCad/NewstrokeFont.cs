namespace Schgen.Core.KiCad;

/// Rendered-text geometry for KiCad's stroked Newstroke font.
/// Width is just `chars × font_size × CharWidthRatio`.
public static class NewstrokeFont
{
    /// KiCad's default schematic text size (1.27 mm = 50 mil).
    public const double FontSize = 1.27;

    /// Per-char advance width as a multiple of font height.
    public const double CharWidthRatio = 0.95;

    public static double TextWidth(string s, double fontSize = FontSize) =>
        (s?.Length ?? 0) * fontSize * CharWidthRatio;
}
