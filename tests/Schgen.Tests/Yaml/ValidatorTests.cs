using FluentAssertions;
using Schgen.Core.KiCad;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.Yaml;

public class ValidatorTests
{
    private const string DeviceLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "C"
            (property "Reference" "C" (at 0 0 0))
            (property "Value" "C" (at 0 0 0))
            (symbol "C_1_1"
              (pin passive line (at 0  3.81 270) (length 2.794)
                (name "~" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -3.81 90)  (length 2.794)
                (name "~" (effects (font (size 1.27 1.27))))
                (number "2" (effects (font (size 1.27 1.27)))))))
          (symbol "Regulator"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "Regulator" (at 0 0 0))
            (symbol "Regulator_1_1"
              (rectangle (start -5 -5) (end 5 5) (stroke (width 0)) (fill (type background)))
              (pin power_in  line (at -7.62  2.54 0) (length 2.54)
                (name "VIN"  (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin power_in  line (at -7.62 -2.54 0) (length 2.54)
                (name "GND"  (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27)))))
              (pin power_out line (at  7.62  0    180) (length 2.54)
                (name "VOUT" (effects (font (size 1.27 1.27)))) (number "3" (effects (font (size 1.27 1.27))))))))
        """;

    private const string Yaml = """
        power_nets: [GND, VCC_3V3, VBUS_5V]
        sheets:
          power:
            components:
              - ref: U1
                symbol: Device:Regulator
                footprint: Package_TO:SOT-23
                pins:
                  VIN: VBUS_5V
                  VOUT: VCC_3V3
              - ref: C1
                symbol: Device:C
                footprint: Capacitor_SMD:C_0805
                pins:
                  1: VBUS_5V
                  2: GND
        root:
          instantiate:
            - sheet: power
        """;

    private static LibraryIndex MakeLibs() =>
        LibraryIndex.FromLibraries(
            new[] { SymbolLibrary.FromText(DeviceLib, "Device") },
            new[] { StubFootprintLibrary("Package_TO", "SOT-23"),
                    StubFootprintLibrary("Capacitor_SMD", "C_0805") });

    private static FootprintLibrary StubFootprintLibrary(string nickname, params string[] footprints)
    {
        // FootprintLibrary.LoadDir derives the nickname from the directory's
        // basename (minus .pretty), so the .pretty dir must literally be named
        // <nickname>.pretty. Put it under a unique parent so parallel test
        // runs don't collide on the same nickname.
        var parent = Path.Combine(Path.GetTempPath(), $"schgen-test-{Guid.NewGuid():N}");
        var dir = Path.Combine(parent, $"{nickname}.pretty");
        Directory.CreateDirectory(dir);
        foreach (var fp in footprints)
        {
            var path = Path.Combine(dir, $"{fp}.kicad_mod");
            File.WriteAllText(path, $"(footprint \"{fp}\" (layer \"F.Cu\"))");
        }
        return FootprintLibrary.LoadDir(dir);
    }

    [Fact]
    public void Clean_input_validates_with_no_errors()
    {
        var doc = YamlLoader.LoadText(Yaml);
        var libs = MakeLibs();
        var r = Validator.Validate(doc, libs);
        r.Errors.Should().BeEmpty();
        r.Ok.Should().BeTrue();
    }

    [Fact]
    public void Unknown_symbol_is_error()
    {
        const string Bad = """
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:DoesNotExist
                    pins: { 1: GND }
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Bad);
        var r = Validator.Validate(doc, MakeLibs());
        r.Errors.Should().ContainMatch("*Device:DoesNotExist*not found*");
    }

    [Fact]
    public void Unknown_pin_is_error()
    {
        const string Bad = """
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { NOSUCH: GND }
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Bad);
        var r = Validator.Validate(doc, MakeLibs());
        r.Errors.Should().ContainMatch("*no pin named or numbered 'NOSUCH'*");
    }

    [Fact]
    public void Refdes_collision_within_sheet_is_error()
    {
        const string Bad = """
            sheets:
              a:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V, 2: GND, 3: VCC_3V3 }
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V, 2: GND, 3: VCC_3V3 }
            root:
              instantiate:
                - sheet: a
            """;
        var doc = YamlLoader.LoadText(Bad);
        var r = Validator.Validate(doc, MakeLibs());
        r.Errors.Should().ContainMatch("*refdes collision*'U1'*");
    }

    [Fact]
    public void Same_ref_across_sheets_is_allowed()
    {
        const string Ok = """
            sheets:
              a:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V, 2: GND, 3: VCC_3V3 }
              b:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V, 2: GND, 3: VCC_3V3 }
            root:
              instantiate:
                - sheet: a
                - sheet: b
            """;
        var doc = YamlLoader.LoadText(Ok);
        var r = Validator.Validate(doc, MakeLibs());
        r.Errors.Should().NotContainMatch("*refdes collision*");
    }

    [Fact]
    public void Multi_instantiated_sheet_without_template_is_error()
    {
        const string Bad = """
            sheets:
              port:
                components:
                  - ref: J1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V, 2: GND, 3: VCC_3V3 }
            root:
              instantiate:
                - { sheet: port, as: P1 }
                - { sheet: port, as: P2 }
            """;
        var doc = YamlLoader.LoadText(Bad);
        var r = Validator.Validate(doc, MakeLibs());
        r.Errors.Should().ContainMatch("*'port' is instantiated 2 times but is not marked template:true*");
    }

    // DeviceLib + a two-unit symbol where pin K2 exists in BOTH units
    // with different pin names (unit 1: VSS; unit 2: VDD). Used to assert
    // that each unit's pins resolve independently.
    private const string DeviceLibWithDual = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "C"
            (property "Reference" "C" (at 0 0 0))
            (property "Value" "C" (at 0 0 0))
            (symbol "C_1_1"
              (pin passive line (at 0  3.81 270) (length 2.794)
                (name "~" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -3.81 90)  (length 2.794)
                (name "~" (effects (font (size 1.27 1.27))))
                (number "2" (effects (font (size 1.27 1.27)))))))
          (symbol "Regulator"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "Regulator" (at 0 0 0))
            (symbol "Regulator_1_1"
              (rectangle (start -5 -5) (end 5 5))
              (pin power_in  line (at -7.62  2.54 0) (length 2.54)
                (name "VIN"  (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin power_in  line (at -7.62 -2.54 0) (length 2.54)
                (name "GND"  (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27)))))
              (pin power_out line (at  7.62  0    180) (length 2.54)
                (name "VOUT" (effects (font (size 1.27 1.27)))) (number "3" (effects (font (size 1.27 1.27)))))))
          (symbol "DUAL"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "DUAL" (at 0 0 0))
            (symbol "DUAL_1_1"
              (rectangle (start -5 -5) (end 5 5))
              (pin power_in line (at -7.62 2.54 0) (length 2.54)
                (name "VSS" (effects (font (size 1.27 1.27))))
                (number "K2" (effects (font (size 1.27 1.27))))))
            (symbol "DUAL_2_1"
              (rectangle (start -5 -5) (end 5 5))
              (pin power_in line (at -7.62 -2.54 0) (length 2.54)
                (name "VDD" (effects (font (size 1.27 1.27))))
                (number "K2" (effects (font (size 1.27 1.27))))))))
        """;

    private static LibraryIndex MakeLibsWithDual() =>
        LibraryIndex.FromLibraries(
            new[] { SymbolLibrary.FromText(DeviceLibWithDual, "Device") },
            new[] { StubFootprintLibrary("Package_TO", "SOT-23"),
                    StubFootprintLibrary("Capacitor_SMD", "C_0805") });

    [Fact]
    public void Pin_in_other_unit_is_reported_as_missing()
    {
        // Component declares unit 1, but only unit 2 has the pin name
        // attribution that's not number-matched. Validator must reject.
        const string Bad = """
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:DUAL
                    footprint: Package_TO:SOT-23
                    unit: 1
                    pins: { VDD: V1P5 }
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Bad);
        var r = Validator.Validate(doc, MakeLibsWithDual());
        // Unit 1's pin is named VSS, not VDD - so 'VDD' isn't found on unit 1.
        r.Errors.Should().ContainMatch("*no pin named or numbered 'VDD'*");
    }

    [Fact]
    public void Same_pin_number_resolves_per_unit()
    {
        // Two component entries, same refdes, different units. Each
        // unit's K2 has a different pin name. Both attributions must
        // succeed without merging.
        const string Ok = """
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:DUAL
                    footprint: Package_TO:SOT-23
                    unit: 1
                    pins: { K2: GND }
                  - ref: U1
                    symbol: Device:DUAL
                    footprint: Package_TO:SOT-23
                    unit: 2
                    pins: { K2: V1P5 }
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Ok);
        var r = Validator.Validate(doc, MakeLibsWithDual());
        r.Errors.Should().NotContainMatch("*no pin named or numbered 'K2'*");
        r.Errors.Should().NotContainMatch("*refdes collision*");
    }

    [Fact]
    public void NC_bulk_form_parses_and_routes_listed_pins_to_NC()
    {
        const string Yml = """
            power_nets: [GND, V1P5]
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:DUAL
                    footprint: Package_TO:SOT-23
                    unit: 1
                    pins:
                      NC: [K2]
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Yml);
        var libs = MakeLibsWithDual();
        // Drive auto-bind (normally invoked inside FromDocument; here we
        // call it directly because libs are constructed in-memory).
        LibraryIndex.ApplyPowerNetPinAutoBind(doc, libs);

        var u1 = doc.Sheets["power"].Components.Single(c => c.Ref == "U1");
        u1.Pins.Should().ContainKey("K2");
        u1.Pins["K2"].Should().BeEquivalentTo(new[] { "NC" });
        // Validator should accept the YAML.
        var r = Validator.Validate(doc, libs);
        r.Errors.Should().BeEmpty();
    }

    [Fact]
    public void NC_explicit_override_wins_over_auto_bind()
    {
        // Unit 1's K2 has pin name "VSS" - power_net_aliases maps VSS to
        // GND, so auto-bind would normally set K2 -> GND. The explicit
        // `NC: [K2]` in YAML must take precedence and prevent the bind.
        const string Yml = """
            power_nets: [GND]
            power_net_aliases:
              GND: [VSS]
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:DUAL
                    footprint: Package_TO:SOT-23
                    unit: 1
                    pins:
                      NC: [K2]
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Yml);
        var libs = MakeLibsWithDual();
        LibraryIndex.ApplyPowerNetPinAutoBind(doc, libs);

        var u1 = doc.Sheets["power"].Components.Single(c => c.Ref == "U1");
        u1.Pins["K2"].Should().BeEquivalentTo(new[] { "NC" });
        u1.Pins["K2"].Should().NotContain("GND");
    }

    [Fact]
    public void Per_pin_NC_form_continues_to_work()
    {
        const string Yml = """
            power_nets: [GND]
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:DUAL
                    footprint: Package_TO:SOT-23
                    unit: 1
                    pins: { K2: NC }
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Yml);
        var libs = MakeLibsWithDual();
        LibraryIndex.ApplyPowerNetPinAutoBind(doc, libs);

        var u1 = doc.Sheets["power"].Components.Single(c => c.Ref == "U1");
        u1.Pins["K2"].Should().BeEquivalentTo(new[] { "NC" });
    }

    [Fact]
    public void Auto_bind_fills_in_unattributed_power_pin()
    {
        // No explicit YAML entry for K2 (named VSS in unit 1). Auto-bind
        // must fill it in from the VSS -> GND alias.
        const string Yml = """
            power_nets: [GND]
            power_net_aliases:
              GND: [VSS]
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:DUAL
                    footprint: Package_TO:SOT-23
                    unit: 1
                    pins: {}
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Yml);
        var libs = MakeLibsWithDual();
        LibraryIndex.ApplyPowerNetPinAutoBind(doc, libs);

        var u1 = doc.Sheets["power"].Components.Single(c => c.Ref == "U1");
        u1.Pins.Should().ContainKey("K2");
        u1.Pins["K2"].Should().BeEquivalentTo(new[] { "GND" });
    }

    [Fact]
    public void Redundant_explicit_power_attribution_is_error()
    {
        // Pin GND on the Regulator symbol is named 'GND'. Attributing it
        // to GND in YAML is redundant - auto-bind would produce the same
        // result. Audit must flag it.
        const string Yml = """
            power_nets: [GND, VBUS_5V, VCC_3V3]
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins:
                      VIN: VBUS_5V
                      GND: GND
                      VOUT: VCC_3V3
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Yml);
        var libs = MakeLibs();
        LibraryIndex.ApplyPowerNetPinAutoBind(doc, libs);
        var r = Validator.Validate(doc, libs);
        r.Errors.Should().ContainMatch("*redundant explicit attribution to power_net 'GND'*pin name 'GND'*");
    }

    [Fact]
    public void Conflicting_power_attribution_is_error()
    {
        // Pin GND named 'GND' attributed to VBUS_5V is wrong - audit flags
        // the mismatch between pin name and the YAML's chosen power_net.
        const string Yml = """
            power_nets: [GND, VBUS_5V, VCC_3V3]
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins:
                      VIN: VBUS_5V
                      GND: VBUS_5V
                      VOUT: VCC_3V3
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Yml);
        var libs = MakeLibs();
        LibraryIndex.ApplyPowerNetPinAutoBind(doc, libs);
        var r = Validator.Validate(doc, libs);
        r.Errors.Should().ContainMatch("*YAML attributes pin to 'VBUS_5V'*'GND' auto-binds to 'GND'*");
    }

    [Fact]
    public void Auto_bound_entries_are_not_audit_targets()
    {
        // Removing the YAML's GND attribution lets auto-bind synthesize it.
        // The audit must NOT flag the auto-bound entry as redundant - it
        // only audits entries the user actually wrote.
        const string Yml = """
            power_nets: [GND, VBUS_5V, VCC_3V3]
            sheets:
              power:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins:
                      VIN: VBUS_5V
                      VOUT: VCC_3V3
            root:
              instantiate:
                - sheet: power
            """;
        var doc = YamlLoader.LoadText(Yml);
        var libs = MakeLibs();
        LibraryIndex.ApplyPowerNetPinAutoBind(doc, libs);

        var u1 = doc.Sheets["power"].Components.Single(c => c.Ref == "U1");
        u1.Pins["2"].Single().Should().Be("GND");
        u1.AutoBoundPinIds.Should().Contain("2");

        var r = Validator.Validate(doc, libs);
        r.Errors.Should().NotContainMatch("*redundant*");
    }

    [Fact]
    public void Param_for_unknown_port_is_error()
    {
        const string Bad = """
            sheets:
              port:
                template: true
                ports:
                  VBUS: { dir: power_in }
                components: []
            root:
              instantiate:
                - sheet: port
                  as: P1
                  params:
                    NOSUCH: foo
            """;
        var doc = YamlLoader.LoadText(Bad);
        var r = Validator.Validate(doc, MakeLibs());
        r.Errors.Should().ContainMatch("*passes param 'NOSUCH'*has no such port*");
    }
}
