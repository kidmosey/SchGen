using FluentAssertions;
using Schgen.Core.Bom;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.Bom;

public class BomBuilderTests
{
    private const string Yaml = """
        sheets:
          main:
            components:
              - ref: R1
                symbol: Device:R
                footprint: Resistor_SMD:R_0402
                value: "10k"
                mpn: RC0402FR-0710KL
                manufacturer: Yageo
                pins: { 1: A, 2: B }
              - ref: R2
                symbol: Device:R
                footprint: Resistor_SMD:R_0402
                value: "10k"
                mpn: RC0402FR-0710KL
                manufacturer: Yageo
                pins: { 1: A, 2: B }
              - ref: U1
                symbol: Rockchip:RK3566
                footprint: Rockchip:RK3566_BGA565
                unit: 1
                value: "RK3566"
                mpn: RK3566
                manufacturer: Rockchip
                pins: { 1A1: GND }
              - ref: U1
                symbol: Rockchip:RK3566
                footprint: Rockchip:RK3566_BGA565
                unit: 2
                pins: { 1B2: GND }
        root:
          instantiate:
            - sheet: main
        """;

    [Fact]
    public void Groups_identical_parts_and_dedupes_multiunit()
    {
        var doc = YamlLoader.LoadText(Yaml);
        YamlLoader.ConsolidateMultiUnitFields(doc);
        var bom = BomBuilder.Build(doc);

        // R1+R2 collapse to one line qty 2; U1 (2 units) is one physical part qty 1.
        var r = bom.Single(l => l.Part == "10k");
        r.Qty.Should().Be(2);
        r.Refs.Should().BeEquivalentTo(new[] { "R1", "R2" });
        r.Mpn.Should().Be("RC0402FR-0710KL");

        var u = bom.Single(l => l.Part == "RK3566");
        u.Qty.Should().Be(1);                       // multi-unit counted once
        u.Mpn.Should().Be("RK3566");                // consolidated from unit 1

        BomBuilder.ToCsv(bom).Should().StartWith("qty,part,value,footprint,mpn,manufacturer,dnp,refdes");
    }

    [Fact]
    public void Parts_catalog_supplies_mpn_when_component_has_none()
    {
        const string Y = """
            parts:
              "10k": { mpn: RC0402FR-0710KL, manufacturer: Yageo }
              "USBLC6-2P6": { mpn: USBLC6-2P6, manufacturer: STMicroelectronics }
            sheets:
              s:
                components:
                  - ref: R1
                    symbol: Device:R
                    footprint: Resistor_SMD:R_0402
                    value: "10k"
                    pins: { 1: A }
                  - ref: U1
                    symbol: Power_Protection:USBLC6-2P6
                    footprint: SOT-23-6
                    pins: { 1: A }
            root:
              instantiate:
                - sheet: s
            """;
        var doc = YamlLoader.LoadText(Y);
        var bom = BomBuilder.Build(doc);
        bom.Single(l => l.Part == "10k").Mpn.Should().Be("RC0402FR-0710KL");
        bom.Single(l => l.Part == "USBLC6-2P6").Manufacturer.Should().Be("STMicroelectronics");
    }

    [Fact]
    public void Symbol_derived_part_name_when_value_absent()
    {
        const string Y = """
            sheets:
              s:
                components:
                  - ref: U9
                    symbol: Power_Protection:USBLC6-2P6
                    footprint: SOT-23-6
                    pins: { 1: A }
            root:
              instantiate:
                - sheet: s
            """;
        var doc = YamlLoader.LoadText(Y);
        var bom = BomBuilder.Build(doc);
        bom.Single().Part.Should().Be("USBLC6-2P6");   // derived from symbol, not blank
    }
}
