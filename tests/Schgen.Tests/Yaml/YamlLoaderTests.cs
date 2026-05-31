using FluentAssertions;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.Yaml;

public class YamlLoaderTests
{
    private const string Minimal = """
        libraries:
          - hardware/kicad/symbols/Device.kicad_sym

        config:
          page_size: A3
          title: "Test Board"
          rev: "A1"
          designer: "Craig"
          grid: 50

        power_nets: [GND, VCC_3V3]

        sheets:
          power:
            components:
              - ref: U1
                symbol: Device:Regulator
                footprint: Package_TO_SOT_SMD:SOT-23-3
                pins:
                  VIN: VBUS_5V
                  GND: GND
                  VOUT: VCC_3V3
              - ref: C1
                symbol: Device:C
                value: 10uF
                footprint: Capacitor_SMD:C_0805
                pins:
                  1: VBUS_5V
                  2: GND

        root:
          instantiate:
            - sheet: power
        """;

    [Fact]
    public void Parses_top_level_keys()
    {
        var doc = YamlLoader.LoadText(Minimal);
        doc.Libraries.Should().HaveCount(1);
        doc.Config.PageSize.Should().Be("A3");
        doc.Config.Title.Should().Be("Test Board");
        doc.PowerNets.Should().BeEquivalentTo(new[] { "GND", "VCC_3V3" });
        doc.Sheets.Should().ContainKey("power");
        doc.Root.Instantiate.Should().HaveCount(1);
        doc.Root.Instantiate[0].Sheet.Should().Be("power");
    }

    [Fact]
    public void Parses_components_and_pins()
    {
        var doc = YamlLoader.LoadText(Minimal);
        var power = doc.Sheets["power"];
        power.Components.Should().HaveCount(2);
        var u1 = power.Components.Single(c => c.Ref == "U1");
        u1.Symbol.Should().Be("Device:Regulator");
        u1.Pins["VIN"].Single().Should().Be("VBUS_5V");
        u1.Pins["GND"].Single().Should().Be("GND");

        var c1 = power.Components.Single(c => c.Ref == "C1");
        c1.Value.Should().Be("10uF");
        c1.Pins["1"].Single().Should().Be("VBUS_5V");
        c1.Pins["2"].Single().Should().Be("GND");
    }

    [Fact]
    public void Parses_template_with_ports_and_params()
    {
        const string TemplateYaml = """
            sheets:
              usb_port:
                template: true
                ports:
                  VBUS: { dir: power_in }
                  GND:  { dir: passive }
                  DP:   { dir: bidir }
                  DM:   { dir: bidir }
                components:
                  - ref: J1
                    symbol: Connector:USB_A
                    footprint: Connector_USB:USB_A
                    pins:
                      VBUS: VBUS
                      D+: DP
                      D-: DM
                      GND: GND

            root:
              instantiate:
                - sheet: usb_port
                  as: USB1
                  params:
                    VBUS: VBUS_USB1
                - sheet: usb_port
                  as: USB2
                  params:
                    VBUS: VBUS_USB2
            """;
        var doc = YamlLoader.LoadText(TemplateYaml);
        var usb = doc.Sheets["usb_port"];
        usb.Template.Should().BeTrue();
        usb.Ports.Should().HaveCount(4);
        usb.Ports["DP"].Dir.Should().Be("bidir");

        doc.Root.Instantiate.Should().HaveCount(2);
        doc.Root.Instantiate[0].As.Should().Be("USB1");
        doc.Root.Instantiate[0].Params["VBUS"].Should().Be("VBUS_USB1");
    }

    [Fact]
    public void Parses_bulk_pin_form()
    {
        const string BulkYaml = """
            sheets:
              soc:
                components:
                  - ref: U_SOC
                    symbol: Rockchip:RK3566
                    footprint: Package_BGA:FCBGA-450
                    pins:
                      bulk:
                        VCC_1V8: [A1, A2, A3]
                        GND:     [B1, B2, B3, B4]
                      named:
                        M5: DDR_DQ0
            root:
              instantiate:
                - sheet: soc
            """;
        var doc = YamlLoader.LoadText(BulkYaml);
        var soc = doc.Sheets["soc"].Components[0];
        soc.Pins["A1"].Single().Should().Be("VCC_1V8");
        soc.Pins["A2"].Single().Should().Be("VCC_1V8");
        soc.Pins["A3"].Single().Should().Be("VCC_1V8");
        soc.Pins["B1"].Single().Should().Be("GND");
        soc.Pins["M5"].Single().Should().Be("DDR_DQ0");
    }

    [Fact]
    public void Parses_comma_separated_pin_keys()
    {
        const string Yaml = """
            sheets:
              io:
                components:
                  - ref: J1
                    symbol: Connector:Conn
                    footprint: foo
                    pins:
                      "1, 3, 5": "+5V"
                      "2,4,6": GND
            root:
              instantiate:
                - sheet: io
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var j = doc.Sheets["io"].Components[0];
        j.Pins["1"].Single().Should().Be("+5V");
        j.Pins["3"].Single().Should().Be("+5V");
        j.Pins["5"].Single().Should().Be("+5V");
        j.Pins["2"].Single().Should().Be("GND");
        j.Pins["4"].Single().Should().Be("GND");
        j.Pins["6"].Single().Should().Be("GND");
    }

    [Fact]
    public void Parses_range_pin_keys()
    {
        const string Yaml = """
            sheets:
              io:
                components:
                  - ref: J1
                    symbol: Connector:Conn
                    footprint: foo
                    pins:
                      "97-100": GND
                      "200-202, 210, 220-221": GND
            root:
              instantiate:
                - sheet: io
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var j = doc.Sheets["io"].Components[0];
        new[] { "97", "98", "99", "100", "200", "201", "202", "210", "220", "221" }
            .Should().AllSatisfy(p => j.Pins[p].Single().Should().Be("GND"));
    }

    [Fact]
    public void Range_keys_work_in_bulk_form()
    {
        const string Yaml = """
            sheets:
              io:
                components:
                  - ref: J1
                    symbol: Connector:Conn
                    footprint: foo
                    pins:
                      bulk:
                        GND: ["2-4", "8"]
                        "+5V": "1, 5-6"
            root:
              instantiate:
                - sheet: io
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var j = doc.Sheets["io"].Components[0];
        j.Pins["2"].Single().Should().Be("GND");
        j.Pins["3"].Single().Should().Be("GND");
        j.Pins["4"].Single().Should().Be("GND");
        j.Pins["8"].Single().Should().Be("GND");
        j.Pins["1"].Single().Should().Be("+5V");
        j.Pins["5"].Single().Should().Be("+5V");
        j.Pins["6"].Single().Should().Be("+5V");
    }

    [Fact]
    public void Non_numeric_pin_ids_pass_through_unchanged()
    {
        const string Yaml = """
            sheets:
              io:
                components:
                  - ref: J1
                    symbol: Connector:USB
                    footprint: foo
                    pins:
                      "D+, D-": NC
                      VBUS: "+5V"
            root:
              instantiate:
                - sheet: io
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var j = doc.Sheets["io"].Components[0];
        j.Pins["D+"].Single().Should().Be("NC");
        j.Pins["D-"].Single().Should().Be("NC");
        j.Pins["VBUS"].Single().Should().Be("+5V");
    }

    [Fact]
    public void Parses_net_to_pins_reverse_form()
    {
        const string Yaml = """
            sheets:
              io:
                components:
                  - ref: J1
                    symbol: Connector:Conn
                    footprint: foo
                    pins:
                      1: PROG_PRESENT_N
                      3: "+12V"
                      GND: ["2-8/2", 140, 142]
                      "+5V": ["5, 71"]
            root:
              instantiate:
                - sheet: io
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var j = doc.Sheets["io"].Components[0];
        j.Pins["1"].Single().Should().Be("PROG_PRESENT_N");
        j.Pins["3"].Single().Should().Be("+12V");
        new[] { "2", "4", "6", "8", "140", "142" }
            .Should().AllSatisfy(p => j.Pins[p].Single().Should().Be("GND"));
        j.Pins["5"].Single().Should().Be("+5V");
        j.Pins["71"].Single().Should().Be("+5V");
    }

    [Fact]
    public void Stepped_range_expansion()
    {
        const string Yaml = """
            sheets:
              io:
                components:
                  - ref: J1
                    symbol: Connector:Conn
                    footprint: foo
                    pins:
                      "2-10/2": GND
                      "1-11/4": "+5V"
            root:
              instantiate:
                - sheet: io
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var j = doc.Sheets["io"].Components[0];
        new[] { "2", "4", "6", "8", "10" }
            .Should().AllSatisfy(p => j.Pins[p].Single().Should().Be("GND"));
        new[] { "1", "5", "9" }
            .Should().AllSatisfy(p => j.Pins[p].Single().Should().Be("+5V"));
    }

    [Fact]
    public void Host_field_parses_into_ComponentDef()
    {
        const string Yaml = """
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:R
                    footprint: foo
                    pins: { 1: SIG, 2: GND }
                  - ref: R1
                    symbol: Device:R
                    footprint: foo
                    host: U1.1
                    pins: { 1: SIG, 2: "+3V3" }
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var r1 = doc.Sheets["power"].Components.Single(c => c.Ref == "R1");
        r1.Host.Should().Be("U1.1");
        // Pre-patch: still on the original net
        r1.Pins["1"].Single().Should().Be("SIG");
    }

    [Fact]
    public void ApplyHostStubNets_rewrites_anchor_and_appends_to_host()
    {
        // Hand-build a doc since LoadText doesn't resolve symbols.
        var doc = new CircuitDocument();
        doc.PowerNets.Add("GND");
        var sheet = new SheetDef { Name = "s" };
        sheet.Components.Add(new ComponentDef
        {
            Ref = "U1",
            Symbol = "X:Y",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["11"] = new() { "ETH_AVDD33" },
            },
        });
        sheet.Components.Add(new ComponentDef
        {
            Ref = "C1",
            Symbol = "Device:C",
            Host = "U1.11",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["1"] = new() { "ETH_AVDD33" },
                ["2"] = new() { "GND" },
            },
        });
        doc.Sheets["s"] = sheet;

        LibraryIndex.ApplyHostStubNets(doc);

        // C1's anchor pin (pin 1) now carries the rail AND the stub - multi-
        // net so the emitter's "hide stub on multi-net pin" rule kicks in for
        // the cap side, mirroring what already happens on the host pin.
        var c1 = sheet.Components.Single(c => c.Ref == "C1");
        c1.Pins["1"].Should().BeEquivalentTo(new[] { "ETH_AVDD33", "ETH_AVDD33__U1_11" });
        c1.Pins["2"].Single().Should().Be("GND");   // return pin untouched

        // U1.11 has both the original rail AND the stub.
        var u1 = sheet.Components.Single(c => c.Ref == "U1");
        u1.Pins["11"].Should().BeEquivalentTo(new[] { "ETH_AVDD33", "ETH_AVDD33__U1_11" });
    }

    [Fact]
    public void ApplyHostStubNets_idempotent_under_repeated_calls()
    {
        var doc = new CircuitDocument();
        var sheet = new SheetDef { Name = "s" };
        sheet.Components.Add(new ComponentDef
        {
            Ref = "U1", Symbol = "X:Y",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                { ["11"] = new() { "VCC" } },
        });
        sheet.Components.Add(new ComponentDef
        {
            Ref = "C1", Symbol = "Device:C", Host = "U1.11",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["1"] = new() { "VCC" }, ["2"] = new() { "GND" },
            },
        });
        doc.Sheets["s"] = sheet;

        LibraryIndex.ApplyHostStubNets(doc);
        LibraryIndex.ApplyHostStubNets(doc);   // second call

        var u1 = sheet.Components.Single(c => c.Ref == "U1");
        u1.Pins["11"].Should().BeEquivalentTo(new[] { "VCC", "VCC__U1_11" });
        var c1 = sheet.Components.Single(c => c.Ref == "C1");
        // Cap-side pin is also multi-net (rail + stub) after the append.
        c1.Pins["1"].Should().BeEquivalentTo(new[] { "VCC", "VCC__U1_11" });
    }

    [Fact]
    public void Validator_reports_host_with_missing_refdes()
    {
        var doc = new CircuitDocument();
        var sheet = new SheetDef { Name = "s" };
        sheet.Components.Add(new ComponentDef
        {
            Ref = "C1", Symbol = "Device:C", Host = "U_NOPE.11",
            Footprint = "fp:any",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["1"] = new() { "X" }, ["2"] = new() { "GND" },
            },
        });
        doc.Sheets["s"] = sheet;
        var libs = LibraryIndex.FromLibraries();   // empty -> all symbol lookups null

        var report = Validator.Validate(doc, libs);
        report.Errors.Should().Contain(e => e.Contains("no component 'U_NOPE'"));
    }

    [Fact]
    public void Validator_reports_host_with_unassigned_pin()
    {
        var doc = new CircuitDocument();
        var sheet = new SheetDef { Name = "s" };
        sheet.Components.Add(new ComponentDef
        {
            Ref = "U1", Symbol = "X:Y", Footprint = "fp:any",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                { ["10"] = new() { "FOO" } },  // pin 11 NOT assigned
        });
        sheet.Components.Add(new ComponentDef
        {
            Ref = "C1", Symbol = "Device:C", Host = "U1.11", Footprint = "fp:any",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["1"] = new() { "FOO" }, ["2"] = new() { "GND" },
            },
        });
        doc.Sheets["s"] = sheet;
        var libs = LibraryIndex.FromLibraries();

        var report = Validator.Validate(doc, libs);
        report.Errors.Should().Contain(e => e.Contains("no net assigned on pin '11'"));
    }

    [Fact]
    public void Validator_reports_host_with_no_matching_net_on_passive()
    {
        var doc = new CircuitDocument();
        var sheet = new SheetDef { Name = "s" };
        sheet.Components.Add(new ComponentDef
        {
            Ref = "U1", Symbol = "X:Y", Footprint = "fp:any",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                { ["11"] = new() { "FOO" } },
        });
        sheet.Components.Add(new ComponentDef
        {
            Ref = "C1", Symbol = "Device:C", Host = "U1.11", Footprint = "fp:any",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["1"] = new() { "BAR" },     // doesn't match host net 'FOO'
                ["2"] = new() { "GND" },
            },
        });
        doc.Sheets["s"] = sheet;
        var libs = LibraryIndex.FromLibraries();

        var report = Validator.Validate(doc, libs);
        report.Errors.Should().Contain(e => e.Contains("no pin on any of host pin 11's nets (FOO)"));
    }

    [Fact]
    public void Validator_reports_malformed_host_string()
    {
        var doc = new CircuitDocument();
        var sheet = new SheetDef { Name = "s" };
        sheet.Components.Add(new ComponentDef
        {
            Ref = "C1", Symbol = "Device:C", Host = "no_dot_here", Footprint = "fp:any",
            Pins = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["1"] = new() { "X" }, ["2"] = new() { "GND" },
            },
        });
        doc.Sheets["s"] = sheet;
        var libs = LibraryIndex.FromLibraries();

        var report = Validator.Validate(doc, libs);
        report.Errors.Should().Contain(e => e.Contains("malformed"));
    }

    [Fact]
    public void Parses_overrides()
    {
        const string OverrideYaml = """
            sheets:
              io:
                components:
                  - ref: J_EDGE
                    symbol: Connector:DSUB
                    footprint: Connector_Dsub:DSUB-9
                    pcb_at: [10.0, 50.0]
                    pcb_rotate: 90
                    sch_at: [120, 80]
                    dnp: false
                    pins:
                      1: SIG1
            root:
              instantiate:
                - sheet: io
            """;
        var doc = YamlLoader.LoadText(OverrideYaml);
        var j = doc.Sheets["io"].Components[0];
        j.PcbAt.Should().Be((10.0, 50.0));
        j.PcbRotate.Should().Be(90);
        j.SchAt.Should().Be((120.0, 80.0));
        j.Dnp.Should().BeFalse();
    }
}
