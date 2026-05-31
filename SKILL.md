---
name: schgen
description: Use when the user wants to author, edit, build, or debug KiCad schematics + initial PCB placement from a YAML circuit description. Triggers on requests to create a new board / sheet / sub-circuit, add or wire a component, regenerate KiCad files, validate a circuit, or look up symbol pins. Don't use for hand-editing existing .kicad_sch / .kicad_pcb files — schgen owns those outputs end-to-end.
---

# schgen — YAML → KiCad 10 generator

## What it is

`schgen` is a .NET 9 CLI app that consumes a YAML circuit description and emits a complete, ERC-clean KiCad 10 hierarchical schematic plus an initial `.kicad_pcb` with all footprints placed in clusters matching the schematic's logical groupings. The user does board outline, stackup, net classes, diff pairs, length matching, routing, and pours in KiCad after schgen has populated the board with parts.

Source of truth is the YAML. Never hand-edit `.kicad_sch` / `.kicad_pcb` files — regenerate via `schgen build`.

## Invocation

After `dotnet tool install --global schgen` the CLI is on `$PATH`:

```bash
# Build a project (writes <projectName>.kicad_pro + per-sheet .kicad_sch + .kicad_pcb)
schgen build path/to/board.yaml --out out/dir/

# Validate without writing
schgen validate path/to/board.yaml

# Dump per-symbol pin map (use this when authoring YAML for an unfamiliar part)
schgen symbols path/to/Lib.kicad_sym
```

Without an install, run directly from a SchGen checkout:

```bash
dotnet run --project /path/to/SchGen/src/Schgen.Cli -- build path/to/board.yaml --out out/dir/
```

The CLI exits 0 on success, 1 on validation failure, 2 on argument errors.

## Mental model

- **Pin attribution**: each component declares `pins: { pin_id → net_name }`. Nets emerge from the union of pin attributions. There is no separate `nets:` block — never invent one.
- **Implicit NC**: any pin not listed on a component is automatically a no-connect marker. Don't add explicit NCs unless you want to be explicit about it (`pin_id: NC`).
- **Sheet partitioning**: the YAML pre-assigns each component to a sheet. The placer auto-positions within the sheet, but never reassigns.
- **Template instances**: a sheet marked `template: true` with declared `ports:` can be instantiated N times at the root. Each instance's `params:` remaps port names to actual nets.

## YAML schema (canonical)

```yaml
libraries:                              # .kicad_sym files
  - hardware/kicad/symbols/Device.kicad_sym

footprint_libs:                         # .pretty dirs
  - hardware/kicad/footprints/

config:
  page_size: A3                         # A3, A4, USLetter, ...
  title: "My Board"
  rev: "A1"
  designer: "Craig"
  grid: 50                              # placer grid in mils (default 50 = ~12.7mm)
  strict_erc: false                     # if true, warnings become errors at build
  pcb_sheet_spacing: 20                 # mm between sheet placement regions on PCB

power_nets: [GND, VCC_3V3, VCC_5V]      # auto-PWR_FLAG when no driver, global labels

includes:                               # split big boards across files (paths
  - sheets/power.yaml                   # are relative to the file that lists them)
  - sheets/soc.yaml

sheets:                                 # singleton + template definitions
  power: { ... }
  usb_port: { template: true, ... }

root:
  instantiate:
    - { sheet: power }
    - sheet: usb_port
      as: PORT1
      params: { VBUS: VBUS_PORT1, DP: D_PORT1_P, DM: D_PORT1_M }
```

### Singleton sheet

```yaml
sheets:
  power:
    components:
      - ref: U_PMIC
        symbol: Rockchip:RK809              # "Lib:Symbol"
        footprint: Package_QFP:LQFP-48      # "Lib:Footprint"
        value: "RK809"
        mpn: "RK809-5"                      # optional BOM fields
        manufacturer: "Rockchip"
        tolerance: ""
        voltage: ""
        datasheet: ""
        dnp: false
        pins:
          VIN: VBUS_5V
          GND: GND
          VOUT_3V3: VCC_3V3
          # any pin not listed → automatic NC marker

      - ref: C1
        symbol: Device:C
        footprint: Capacitor_SMD:C_0805_2012Metric
        value: "10uF"
        pins: { 1: VBUS_5V, 2: GND }
```

### Template sheet (instantiable)

```yaml
sheets:
  usb_port:
    template: true
    ports:                          # cross-sheet contract; becomes sheet pins
      VBUS: { dir: power_in }       # power_in | power_out | input | output | bidir | passive
      GND:  { dir: passive }
      DP:   { dir: bidir }
      DM:   { dir: bidir }
    components:
      - ref: J1
        symbol: Connector:USB_A_Vertical
        footprint: Connector_USB:USB_A_Vertical
        pins:
          VBUS: VBUS                # matches port name → wires to sheet pin
          D+:   DP
          D-:   DM
          GND:  GND
```

### Per-component overrides

```yaml
- ref: J_EDGE
  symbol: Connector:DSUB
  footprint: Connector_Dsub:DSUB-9_Horizontal
  pcb_at: [10.0, 50.0]             # fixed PCB position (mm), bypasses placer
  pcb_rotate: 90
  sch_at: [120, 80]                # fixed schematic position (mm)
  pins: { 1: SIG1, 2: SIG2, SHIELD: SHELL }
```

### High pin-count parts (`bulk:` / `named:` mix)

```yaml
- ref: U_SOC
  symbol: Rockchip:RK3566
  footprint: Package_BGA:FCBGA-450
  pins:
    bulk:                          # net → list of pin numbers
      VCC_1V8: [A1, A2, A3, B5, B6]
      GND:     [A4, A5, B1, B2, B3]
    named:                         # pin → net (mixes with bulk)
      M5: DDR_DQ0
      M6: DDR_DQ1
  # any pin not in bulk or named is still auto-NC
```

## How nets work

- **Same net name = same net.** `U1.VOUT → VCC_3V3` and `J_OUT.1 → VCC_3V3` join automatically.
- **Cap proximity rule** (this is the main reason for the granular net naming): if a 2-pin passive (C/R/L/D) has a pin on a net whose only non-passive endpoint on the same sheet is one component, the passive is placed adjacent to that component's pin on both schematic and PCB. Multiple passives sharing the same host pin (e.g. 4× decoupling caps on `U_SOC.VCC_1V8`) fan out as a strip adjacent to the pin.
- **Dedicated stub nets** preserve which pin a cap bypasses: a power rail named `VBUS_5V_U1_1` with U1.VIN + 2 caps as endpoints keeps those caps glued to U1.VIN. A power rail named just `VBUS_5V` shared with many sinks won't trigger proximity (too many non-passive endpoints).
- **PWR_FLAG**: any net with a `power_in` pin and no `power_out` / `output` driver is auto-flagged with an embedded PWR_FLAG symbol on its home sheet. The user does not need to declare these.
- **Label rendering**:
  - Net used inside a template that matches a port name → hierarchical label.
  - Net in `power_nets:` OR auto-flagged with PWR_FLAG → global label.
  - Singleton-sheet net that appears on multiple sheets → global label.
  - Otherwise → local label.

## Workflow for a new board

1. **Find symbols** — list available pin names/numbers for any unfamiliar part:
   ```bash
   schgen symbols path/to/Lib.kicad_sym
   ```
2. **Draft the YAML** — start with one sheet, one IC. Reference `examples/minimal.yaml` for the smallest exerciser and `examples/template_params.yaml` for templates with params (both ship in the SchGen repo).
3. **Validate early and often**:
   ```bash
   schgen validate board.yaml
   ```
   Catches: missing symbol, unknown pin, refdes collision, ambiguous pin name, missing footprint (warning), unused power_net (warning).
4. **Build**:
   ```bash
   schgen build board.yaml --out hardware/kicad/board/
   ```
5. **Run KiCad ERC** to confirm electrical correctness:
   ```bash
   ~/bin/kicad/kicad-10.0.1-1-x86_64.AppImage --appimage-extract-and-run \
       kicad-cli sch erc hardware/kicad/board/board.kicad_sch \
       --severity-error -o /tmp/erc.txt
   ```
   `lib_symbol_mismatch` and `lib_footprint_mismatch` are pre-silenced in the generated `.kicad_pro` since schgen embeds source-verbatim symbols.

## Common gotchas

| Symptom | Cause | Fix |
|---|---|---|
| `symbol 'Foo:Bar' not found in any loaded library` | `libraries:` doesn't list the .kicad_sym containing Foo:Bar | Add the path to `libraries:`. Paths are resolved relative to the YAML file. |
| `symbol 'X' has no pin named or numbered 'Y'` | YAML uses a pin name that doesn't exist on the symbol | Run `schgen symbols` to dump the actual pin names; use pin numbers as a fallback. |
| `pin name 'VSS' is ambiguous on symbol 'X' (N pins share this name)` | Common on BGAs where many pins share `VSS`/`VDD`/`GND` | Use pin numbers in the YAML (`bulk:` form is best for this). |
| `sheet 'foo' is instantiated N times but is not marked template:true` | Reusing a sheet across multiple instances | Add `template: true` and declare `ports:` for the cross-sheet contract. |
| `isolated_pin_label` (ERC warning) on a global label | The net appears at only one pin endpoint in the whole design | Either declare a downstream consumer or accept the warning. |
| Decoupling cap ends up far from its IC | The bypass net has more than one non-passive endpoint, so the proximity rule doesn't fire | Give the cap a dedicated stub net (e.g. `VCC_3V3_U_SOC_A1` instead of generic `VCC_3V3`); put both the IC pin and the cap pin on that stub. |
| `power_pin_not_driven` ERC error | A net has only `power_in` endpoints and isn't in `power_nets:` so didn't get auto-flagged | If it's a real power rail, add to `power_nets:`. The placer auto-emits PWR_FLAG. |

## Code structure (for reference / edits)

Within a SchGen checkout:

- `src/Schgen.Core/Yaml/` — model + loader + validator + library index.
- `src/Schgen.Core/KiCad/` — SExpr DOM, SymbolLibrary, FootprintLibrary, SchematicEmitter, PcbEmitter.
- `src/Schgen.Core/Placement/` — SchPlacer (seeded BFS + cap proximity + collision fan-out) and PcbPlacer (per-sheet region pack).
- `src/Schgen.Cli/Commands/` — build, validate, symbols.
- `tests/Schgen.Tests/` — xUnit tests. Run with `dotnet test`.

When making changes:
- Never regenerate user-authored `.kicad_sch` / `.kicad_pcb` files. schgen-owned outputs live under whichever `--out` directory the user chose.
- Run `dotnet test` before considering any schgen change done. There are no integration tests against live KiCad in CI — verify manually with `kicad-cli sch erc` after building.
