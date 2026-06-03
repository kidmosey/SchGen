namespace Schgen.Core.Yaml;

/// In-memory representation of a parsed circuit YAML document.
/// After Load() returns, this is the canonical input to the placer + emitters.
public sealed class CircuitDocument
{
    public List<string> Libraries { get; init; } = new();
    public List<string> FootprintLibs { get; init; } = new();
    public CircuitConfig Config { get; set; } = new();
    public List<string> PowerNets { get; init; } = new();

    /// Pin-name -> power-net mappings used by the auto-bind pass to fill in
    /// unassigned chip pins. The value side is a list of glob patterns
    /// (trailing `*` supported) matching the lib symbol's pin name. Example:
    ///   power_net_aliases:
    ///     GND: ["VSS", "VSS_*", "AVSS_*", "AVSS1_*"]
    /// is interpreted as "any lib pin whose NAME matches one of these
    /// patterns and that the YAML did not explicitly assign should bind to
    /// net GND". Keeps the YAML free of repetitive `1B10: GND, 1B11: GND ...`
    /// blocks on BGAs without baking a lib-pin-name convention into code.
    public Dictionary<string, List<string>> PowerNetAliases { get; init; } =
        new(StringComparer.Ordinal);

    /// Optional parts catalog: part-name (a component's value, or the symbol
    /// name when value is blank) -> sourcing info. Lets the BOM carry real
    /// MPNs authored once per distinct part instead of repeated on every one of
    /// hundreds of component instances. A component's own `mpn:` still wins.
    public Dictionary<string, PartInfo> Parts { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, SheetDef> Sheets { get; init; } = new(StringComparer.Ordinal);
    public RootSheet Root { get; init; } = new();
    public string SourcePath { get; init; } = "";

    /// Net names that were synthesized by `LibraryIndex.ApplyHostStubNets` to
    /// anchor a `host:`-tagged passive to its chip pin. The emitter treats
    /// these as ALWAYS-LOCAL placement-helper labels: they never get a
    /// PWR_FLAG and never get rendered as `global_label` even when one of
    /// their endpoints is a `power_in` pin. Electrical PWR_FLAGging happens
    /// only on the parent rail (e.g. `ETH_AVDD33`), which the stub aliases.
    public HashSet<string> GeneratedStubNets { get; } = new(StringComparer.Ordinal);
}

public sealed class CircuitConfig
{
    public string PageSize { get; init; } = "A3";
    public string Title { get; init; } = "Untitled";
    public string Rev { get; init; } = "";
    public string Designer { get; init; } = "";
    public double Grid { get; init; } = 50.0;                  // mils
    public bool StrictErc { get; init; } = false;
    public double PcbSheetSpacing { get; init; } = 20.0;       // mm
}

public sealed class SheetDef
{
    public string Name { get; init; } = "";
    public bool Template { get; init; }
    public Dictionary<string, PortDef> Ports { get; init; } = new(StringComparer.Ordinal);
    public List<ComponentDef> Components { get; init; } = new();
}

public sealed class PartInfo
{
    public string Mpn { get; init; } = "";
    public string Manufacturer { get; init; } = "";
}

public sealed class PortDef
{
    public string Dir { get; init; } = "passive";  // input | output | bidir | power_in | power_out | passive
}

public sealed class ComponentDef
{
    public string Ref { get; init; } = "";
    public string Symbol { get; init; } = "";                // "Lib:Name"
    /// Unit number for multi-unit symbols. Default 1.
    /// Multiple ComponentDefs may share the same Ref iff their Units differ
    /// (this is how a multi-unit chip - e.g. a 11-unit FPGA - is expressed:
    /// one ComponentDef per unit, all with the same Ref).
    public int Unit { get; init; } = 1;
    /// When true (YAML `units: all`), this single ComponentDef stands in for
    /// every unit of its multi-unit symbol: ExpandAllUnits clones it into one
    /// entry per symbol unit (sharing this pins map). Lets a big multi-unit
    /// part (e.g. a 565-ball SoC across 8 sub-units) be wired from one entry,
    /// with by-name pins + power_net_aliases resolving per unit.
    public bool AllUnits { get; init; }
    // Identity / BOM fields below are settable (not init-only) because they
    // belong to the whole symbol, not the unit: `ConsolidateMultiUnitFields`
    // stamps a single canonical value across every unit sharing this Ref so
    // KiCad's annotator doesn't flag "different values for U2A and U2B".
    public string Footprint { get; set; } = "";              // "Lib:Name"
    public string? Value { get; set; }
    public string? Mpn { get; set; }
    public string? Manufacturer { get; set; }
    public string? Tolerance { get; set; }
    public string? Voltage { get; set; }
    public string? Datasheet { get; set; }
    public bool Dnp { get; set; }
    /// pin_id (name or number) -> list of net names. Built from `pins:` and
    /// `pins.bulk:`. The first entry in each list is the canonical electrical
    /// net used for PCB pad-to-net assignment; subsequent entries are
    /// schematic-only "tag" nets emitted as additional labels at the same
    /// pin endpoint. This lets a decoupling cap declare both its shared
    /// power rail (`+3V3`) AND a per-chip tag (`U1_VCC_DECAP`) so the
    /// placer can attach the cap to a specific chip via the 2-endpoint tag
    /// while KiCad still sees the shared rail electrically.
    public Dictionary<string, List<string>> Pins { get; init; } = new(StringComparer.Ordinal);

    /// Pin ids whose entries in `Pins` were INSERTED by
    /// `LibraryIndex.ApplyPowerNetPinAutoBind` rather than declared in the
    /// YAML. Lets the Validator's power-attribution audit distinguish
    /// "the YAML explicitly attributed pin X to power_net Y" (a candidate for
    /// removal if auto-bind would produce the same Y from pin X's name) from
    /// "auto-bind synthesized this entry" (not user-authored, never a target
    /// for the audit).
    public HashSet<string> AutoBoundPinIds { get; } = new(StringComparer.Ordinal);

    /// Optional placement-anchor target for a 2-pin passive. Format:
    /// `<refdes>.<pinid>` (e.g. `U160.11`). When set, a deterministic stub
    /// net `<hostNet>__<hostRef>_<pinId>` is generated post-load: it REPLACES
    /// the passive's matching-rail-side pin net and is APPENDED as a second
    /// label at the host's referenced pin. Net topology then has exactly two
    /// endpoints on the stub (passive + host), giving the placer a single-
    /// mate radial-growth path and the proximity test an anchor-eligible
    /// net. Multi-host passives (e.g. between two chips on a shared signal)
    /// leave this null and stay flat.
    public string? Host { get; init; }
}

public sealed class RootSheet
{
    public List<SheetInstance> Instantiate { get; init; } = new();
}

public sealed class SheetInstance
{
    public string Sheet { get; init; } = "";        // sheet name
    public string? As { get; init; }                // instance label; defaults to Sheet
    public Dictionary<string, string> Params { get; init; } = new(StringComparer.Ordinal);

    public string EffectiveName => string.IsNullOrEmpty(As) ? Sheet : As!;
}
