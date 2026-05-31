# SchGen

YAML → KiCad 10 schematic + initial PCB generator. Authoring boards in YAML, validating them deterministically, and shipping ERC-clean KiCad projects without dragging a mouse through eeschema.

`schgen` consumes a YAML circuit description and emits:

- A complete hierarchical KiCad 10 schematic (`.kicad_sch` per sheet) with embedded library symbols, hierarchical labels at sheet boundaries, power flags, and proximity-placed decoupling caps.
- An initial `.kicad_pcb` with every footprint placed in clusters matching the schematic's logical groupings — ready for stackup, routing, and pours in KiCad.
- A `.kicad_pro` with the right ERC severities pre-silenced for embedded-symbol designs.

The YAML is the source of truth. Re-running `schgen build` reproduces the outputs exactly — never hand-edit the `.kicad_sch` / `.kicad_pcb` files schgen owns.

## Install

As a global .NET tool (recommended):

```bash
git clone <your-fork-or-mirror> ~/Projects/SchGen
cd ~/Projects/SchGen
dotnet pack -c Release src/Schgen.Cli
dotnet tool install --global --add-source ./nupkg SchGen
```

After install, `schgen` is on `$PATH`:

```bash
schgen build path/to/board.yaml --out hardware/kicad/board/
schgen validate path/to/board.yaml
schgen symbols path/to/Lib.kicad_sym
```

Without an install, run from a checkout:

```bash
dotnet run --project src/Schgen.Cli -- build path/to/board.yaml --out hardware/kicad/board/
```

Exit codes: 0 = success, 1 = validation failure, 2 = argument error.

## Quick start

```yaml
# board.yaml
libraries: [examples/libs/Device.kicad_sym, examples/libs/Connector.kicad_sym]
footprint_libs: [examples/footprints/]
config: { page_size: A4, title: "Hello SchGen" }
power_nets: [GND, VCC_3V3]

sheets:
  rc:
    components:
      - { ref: R1, symbol: Device:R, footprint: Device:R_0805,           value: "10k",   pins: { 1: VCC_3V3, 2: NODE } }
      - { ref: C1, symbol: Device:C, footprint: Device:C_0805_2012Metric, value: "100nF", pins: { 1: NODE,    2: GND } }

root:
  instantiate:
    - { sheet: rc }
```

```bash
schgen build board.yaml --out out/
ls out/
# board.kicad_pro  board.kicad_sch  board.kicad_pcb  rc.kicad_sch
```

See [`examples/minimal.yaml`](examples/minimal.yaml) for the smallest exerciser and [`examples/template_params.yaml`](examples/template_params.yaml) for templates with parameter remapping (one filter template, four instantiations).

## Consuming from another project

1. **Install as a global tool** (above). Call `schgen build path/to/your-board.yaml` from anywhere — paths in the YAML resolve relative to the YAML file's own directory.

2. **Wire into a build script:**

   ```bash
   #!/bin/sh
   schgen validate hardware/kicad/main.yaml || exit $?
   schgen build hardware/kicad/main.yaml --out hardware/kicad/main/
   ```

3. **Wire into Claude Code** — symlink (or copy) [SKILL.md](SKILL.md) into your project at `.claude/skills/schgen/SKILL.md` so Claude picks up the schgen skill automatically. The SKILL doc covers the YAML schema, the mental model (pin attribution, implicit NC, template instances), the workflow, and the common gotchas.

## KiCad library setup

`schgen` reads `.kicad_sym` and `.pretty` libraries listed in the YAML. KiCad's stock libraries are not bundled — point at your local KiCad install:

```bash
scripts/install-libs.sh ~/bin/kicad/kicad-10.0.1-1-x86_64.AppImage
```

(That extracts the AppImage's stock libs to `libs/` for use as `libraries:` / `footprint_libs:` paths. The `libs/` directory is gitignored — it's regenerated from a local KiCad install on demand.)

## Repo layout

```
SchGen/
├── README.md                — this file
├── SKILL.md                 — full reference (Claude skill format; symlink into consumers)
├── Schgen.sln               — VS / Rider solution
├── src/
│   ├── Schgen.Cli/          — CLI entrypoint + commands (build/validate/symbols)
│   └── Schgen.Core/         — YAML model + KiCad emitters + placer
├── tests/
│   └── Schgen.Tests/        — xUnit suite
├── examples/                — minimal.yaml, usb_hub.yaml + bundled libs
└── scripts/
    └── install-libs.sh      — stock KiCad lib extractor
```

## Running tests

```bash
dotnet test
```

## License

(unset — add a LICENSE before publishing.)

## Provenance

SchGen was extracted from the MyriadArc cartridge-console project in May 2026 where it generated five production board YAMLs (cart-pcb, cart-programmer, cart-row-pcb, console-board, console-programmer). The full reference docs (YAML schema, label-rendering rules, cap-proximity rules, common gotchas) live in [SKILL.md](SKILL.md).
