using System.Text;
using Schgen.Core.Yaml;

namespace Schgen.Core.Bom;

/// One grouped BOM line: identical parts (same part/value, footprint, MPN, DNP)
/// collapsed into a single row with a quantity and the refdes list.
public sealed record BomLine(
    int Qty,
    string Part,
    string Value,
    string Footprint,
    string Mpn,
    string Manufacturer,
    bool Dnp,
    IReadOnlyList<string> Refs);

/// Builds a grouped bill of materials from a CircuitDocument. Pure (no I/O) so
/// it is unit-testable; the CLI layer handles file output. Expects the document
/// to have already been through ConsolidateMultiUnitFields so every unit of a
/// refdes carries identical Value/Footprint/MPN.
public static class BomBuilder
{
    public static IReadOnlyList<BomLine> Build(CircuitDocument doc)
    {
        // Dedupe by refdes (multi-unit parts collapse to one physical part).
        var byRef = new Dictionary<string, ComponentDef>(StringComparer.Ordinal);
        foreach (var sheet in doc.Sheets.Values)
            foreach (var c in sheet.Components)
                if (!string.IsNullOrEmpty(c.Ref) && !byRef.ContainsKey(c.Ref))
                    byRef[c.Ref] = c;

        static string PartName(ComponentDef c)
        {
            var v = (c.Value ?? "").Trim();
            // Normalize the 0.1uF / 100nF spelling of the same part to one line.
            if (v is "0.1uF" or "100nF" or "0.1uf" or "100nf") return "0.1uF";
            if (v.Length > 0) return v;
            var s = c.Symbol;
            return s.Contains(':') ? s[(s.IndexOf(':') + 1)..] : s;   // symbol-derived
        }

        // Resolve sourcing: a component's own mpn wins; otherwise fall back to
        // the parts catalog keyed by part name.
        (string Mpn, string Mfr) Source(ComponentDef c)
        {
            if (!string.IsNullOrEmpty(c.Mpn)) return (c.Mpn, c.Manufacturer ?? "");
            return doc.Parts.TryGetValue(PartName(c), out var pi) ? (pi.Mpn, pi.Manufacturer) : ("", "");
        }

        var groups = new Dictionary<(string, string, string, bool), List<ComponentDef>>();
        foreach (var c in byRef.Values)
        {
            var key = (PartName(c), c.Footprint ?? "", Source(c).Mpn, c.Dnp);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
            list.Add(c);
        }

        var lines = new List<BomLine>();
        foreach (var ((part, fp, mpn, dnp), members) in groups)
        {
            var refs = members.Select(m => m.Ref).OrderBy(RefKey).ToList();
            var (_, mfr) = Source(members[0]);
            lines.Add(new BomLine(refs.Count, part, members[0].Value ?? "", fp, mpn, mfr, dnp, refs));
        }
        return lines.OrderBy(l => l.Dnp).ThenByDescending(l => l.Qty).ThenBy(l => l.Part, StringComparer.Ordinal).ToList();
    }

    /// Refdes sort key: letter prefix then numeric suffix (R2 before R10).
    private static (string, int) RefKey(string r)
    {
        int i = 0;
        while (i < r.Length && !char.IsDigit(r[i])) i++;
        var prefix = r[..i];
        return (prefix, int.TryParse(r[i..], out var n) ? n : 0);
    }

    public static string ToCsv(IReadOnlyList<BomLine> lines)
    {
        static string Esc(string s) => s.Contains(',') || s.Contains('"')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine("qty,part,value,footprint,mpn,manufacturer,dnp,refdes");
        foreach (var l in lines)
            sb.AppendLine(string.Join(',', new[]
            {
                l.Qty.ToString(), Esc(l.Part), Esc(l.Value), Esc(l.Footprint),
                Esc(l.Mpn), Esc(l.Manufacturer), l.Dnp ? "DNP" : "",
                Esc(string.Join(' ', l.Refs)),
            }));
        return sb.ToString();
    }
}
