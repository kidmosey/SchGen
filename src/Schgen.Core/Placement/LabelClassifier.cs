using Schgen.Core.KiCad;
using Schgen.Core.Yaml;

namespace Schgen.Core.Placement;

/// Determines which `LabelGeometry.Kind` (Regular / Global / Hierarchical) the
/// emitter will produce for each net on each sheet. `FanoutGeometry` uses
/// this to pick the correct rendered label width per net.
///
/// Mirrors the label-type switch in `SchematicEmitter.EmitPinDecorations`.
/// A follow-up should refactor the emitter to call this so they can't drift.
public static class LabelClassifier
{
    /// `{ sheetName -> { netName -> Kind } }` of nets that appear on each
    /// sheet. Nets not in a sheet's map default to `Kind.Regular`.
    public static Dictionary<string, Dictionary<string, LabelGeometry.Kind>> KindsBySheet(
        CircuitDocument doc, LibraryIndex libs)
    {
        var nets = Validator.CollectNetsToPins(doc, libs);

        // Doc-wide PWR-flagged set. A net needs a PWR_FLAG (and emits as
        // global_label) when it has at least one power_in pin endpoint and
        // no power_out / output endpoint. Generated stubs are excluded -
        // their underlying rail carries the flag instead.
        var flagged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (net, ends) in nets)
        {
            if (doc.GeneratedStubNets.Contains(net)) continue;
            bool needsFlag = ends.Any(e => e.Pin.Type == PinType.PowerIn)
                          && !ends.Any(e => e.Pin.Type == PinType.PowerOut || e.Pin.Type == PinType.Output);
            if (needsFlag) flagged.Add(net);
        }

        // Per-net set of sheets the net appears on.
        var netSheets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (net, ends) in nets)
        {
            var s = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in ends) s.Add(e.Sheet);
            netSheets[net] = s;
        }

        var instanceCounts = doc.Root.Instantiate
            .GroupBy(i => i.Sheet)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var result = new Dictionary<string, Dictionary<string, LabelGeometry.Kind>>(StringComparer.Ordinal);
        foreach (var (sheetName, sheet) in doc.Sheets)
        {
            var map = new Dictionary<string, LabelGeometry.Kind>(StringComparer.Ordinal);
            foreach (var net in nets.Keys)
            {
                if (sheet.Template && sheet.Ports.ContainsKey(net))
                {
                    map[net] = LabelGeometry.Kind.Hierarchical;
                    continue;
                }
                if (flagged.Contains(net))
                {
                    map[net] = LabelGeometry.Kind.Global;
                    continue;
                }
                if (instanceCounts.GetValueOrDefault(sheetName, 0) <= 1
                    && netSheets.TryGetValue(net, out var s) && s.Count > 1)
                {
                    map[net] = LabelGeometry.Kind.Global;
                    continue;
                }
                map[net] = LabelGeometry.Kind.Regular;
            }
            result[sheetName] = map;
        }
        return result;
    }
}
