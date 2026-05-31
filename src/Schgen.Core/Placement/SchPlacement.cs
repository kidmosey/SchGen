using Schgen.Core.KiCad;
using Schgen.Core.Yaml;

namespace Schgen.Core.Placement;

/// Result of the schematic placer: one record per component telling the emitter
/// where to drop its symbol on the page.
public sealed class SchPlacement
{
    public Dictionary<string, SheetLayout> Sheets { get; } = new(StringComparer.Ordinal);
}

public sealed class SheetLayout
{
    public string SheetName { get; init; } = "";
    /// Keyed by composite "Ref#Unit" so multi-unit symbols can have
    /// multiple placements sharing the same Ref. Use FindByRef(ref, unit)
    /// for convenience when you only know the ref.
    public Dictionary<string, ComponentPlacement> Components { get; } = new(StringComparer.Ordinal);
    public BBox UsedArea { get; set; } = new(0, 0, 0, 0);

    public ComponentPlacement? FindByRef(string @ref, int unit = 1) =>
        Components.TryGetValue($"{@ref}#{unit}", out var p) ? p : null;
}

public sealed class ComponentPlacement
{
    public string Ref { get; init; } = "";
    /// Unit number for multi-unit symbols (1 for single-unit). Multiple
    /// ComponentPlacement entries can share the same Ref iff their Units
    /// differ - one entry per unit of a multi-unit chip.
    public int Unit { get; init; } = 1;
    /// Composite identifier used as the layout dictionary key.
    public string Key => $"{Ref}#{Unit}";
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }                    // degrees, multiple of 90
    /// Symbol-only bbox translated to (X,Y).
    public BBox BoundingBox { get; set; }
    /// BoundingBox extended in each pin's outward direction by the pin's
    /// label-text width.
    public BBox EffectiveBBox { get; set; }
    /// For anchors: EffectiveBBox unioned with every attached passive's
    /// EffectiveBBox. Implements the "select the component and fan out to
    /// everything wired to it directly" rule - this is the rectangle other
    /// anchors must avoid when BFS picks their position.
    /// For attached passives this equals EffectiveBBox.
    public BBox ClusterBBox { get; set; }
    public SymbolDef Symbol { get; init; } = null!;
}

/// Geometry helpers for label-aware placement. Calibrated against KiCad
/// 10's actual text rendering by measuring real screenshots: characters
/// at the default 1.27 mm font occupy ~0.85 x font width on screen, and
/// the chevron tip + intersheet-ref bracket add ~2.5 mm to a global
/// label. Used by BOTH the placer (for spacing) and the overlap test
/// (for rect computation) so the test predicts what KiCad will draw.
/// Label rendered geometry, derived from KiCad's source primitives:
///   - text width from `NewstrokeFont` per-glyph table.
///   - chevron extent from `SCH_HIERLABEL::CreateGraphicShape` polygon.
///   - text-from-anchor offset from `SCH_LABEL_BASE::GetSchematicTextOffset`.
public static class LabelGeometry
{
    /// KiCad's `SCH_GLOBAL_LABEL::CreateGraphicShape` builds the polygon with
    /// a uniform total bbox width regardless of shape:
    ///   x = LenSize(text) + 2 * margin
    /// where `margin = LabelSizeRatio × font_size` (KiCad default = 0.375).
    /// The shape (BIDIRECTIONAL / INPUT / OUTPUT / PASSIVE) only changes
    /// whether each end of the polygon comes to a chevron point or a flat
    /// edge - the bbox width is the same.
    ///
    /// Perpendicular bbox half-extent = `font_size/2 + margin`.
    public const double LabelSizeRatio = 0.375;
    public static readonly double LabelMargin = LabelSizeRatio * NewstrokeFont.FontSize;
    /// Per-end chevron axial extent. KiCad's polygon vertex at (halfSize, ±y)
    /// is the "kink" where the chevron meets the inner rectangle, so each
    /// chevron region spans [0, halfSize] axially. `halfSize = textHeight/2
    /// + LabelMargin`.
    public static readonly double ChevronHalfSize = NewstrokeFont.FontSize / 2 + LabelMargin;
    /// Asymmetric inside-polygon padding (pin-side smaller than text-side).
    public static readonly double LeftPad  = LabelMargin;
    public static readonly double RightPad = 2.0 * LabelMargin;
    /// Total non-text axial extent for chevron-bearing labels:
    /// chevron_front + leftPad + rightPad + chevron_back.
    public static readonly double ChevronExtent =
        2.0 * ChevronHalfSize + LeftPad + RightPad;

    public enum Kind { Regular, Global, Hierarchical }

    /// Total rendered label width along the rotation axis, in mm. Mirrors
    /// what KiCad's `SCH_LABEL_BASE::GetBoundingBox` would return for an
    /// equivalent label.
    public static double Extent(string netName, Kind kind)
    {
        double text = NewstrokeFont.TextWidth(netName);
        return kind switch
        {
            Kind.Regular      => text,
            Kind.Global       => text + ChevronExtent,
            Kind.Hierarchical => text + ChevronExtent,
            _ => text,
        };
    }

    /// Backward-compatible single-arg overload used by legacy callers.
    /// Conservative default treats labels as `Global` (with chevron).
    public static double Extent(string netName) => Extent(netName, Kind.Global);
}
