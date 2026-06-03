# SchGen

YAML → KiCad 10 schematic + initial PCB generator. Authoring boards in YAML, validating them deterministically, and shipping ERC-clean KiCad projects without dragging a mouse through eeschema.

`schgen` consumes a YAML circuit description and emits:

- A complete hierarchical KiCad 10 schematic (`.kicad_sch` per sheet) with embedded library symbols, hierarchical labels at sheet boundaries, power flags, and proximity-placed decoupling caps.
- An initial `.kicad_pcb` with every footprint placed in clusters matching the schematic's logical groupings — ready for stackup, routing, and pours in KiCad.
- A `.kicad_pro` with the right ERC severities pre-silenced for embedded-symbol designs.

The YAML is the source of truth. Re-running `schgen build` reproduces the outputs exactly — never hand-edit the `.kicad_sch` / `.kicad_pcb` files schgen owns.

## Install

Two one-time steps per machine — after these, every project on the machine can use `schgen` and reference KiCad stock libraries (`Device:R`, `Connector:USB_C`, etc.) without per-project setup.

```bash
# 1. Install the CLI as a global .NET tool.
git clone https://github.com/kidmosey/SchGen.git
cd SchGen
dotnet pack -c Release src/Schgen.Cli
dotnet tool install --global --add-source ./nupkg SchGen

# 2. Extract KiCad's stock symbol + footprint libs to a shared per-user cache
#    at ~/.local/share/schgen/kicad-stock/ (XDG-compliant; %LOCALAPPDATA% on Windows).
#    schgen finds KiCad in standard install locations or any *.AppImage under
#    ~/bin/kicad/, ~/Applications/, ~/Downloads/.
schgen install-stock-libs
```

After that, `schgen` is on `$PATH`:

```bash
schgen build path/to/board.yaml --out hardware/kicad/board/
schgen validate path/to/board.yaml
schgen symbols path/to/Lib.kicad_sym
```

Without a global install, run from a checkout:

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

See [`examples/minimal.yaml`](examples/minimal.yaml) for the smallest exerciser, [`examples/template_params.yaml`](examples/template_params.yaml) for templates with parameter remapping, and [`examples/multi_file/`](examples/multi_file/) for a board split across multiple YAML files using `includes:`.

## Consuming from another project

1. **Install as a global tool** (above). Call `schgen build path/to/your-board.yaml` from anywhere — paths in the YAML resolve relative to the YAML file's own directory.

2. **Wire into a build script:**

   ```bash
   #!/bin/sh
   schgen validate hardware/kicad/main.yaml || exit $?
   schgen build hardware/kicad/main.yaml --out hardware/kicad/main/
   ```

3. **Wire into Claude Code** — symlink (or copy) [SKILL.md](SKILL.md) into your project at `.claude/skills/schgen/SKILL.md` so Claude picks up the schgen skill automatically. The SKILL doc covers the YAML schema, the mental model (pin attribution, implicit NC, template instances), the workflow, and the common gotchas.

## KiCad stock libraries

`schgen install-stock-libs` (above) populates a shared per-user cache once per machine. Projects don't need their own `libs/` copy — schgen's auto-discovery walks:

1. `$XDG_DATA_HOME/schgen/kicad-stock/` (or `~/.local/share/schgen/kicad-stock/` on Linux/macOS, `%LOCALAPPDATA%\schgen\kicad-stock\` on Windows).
2. `$KICAD_DATA` (KiCad's own data-root env var).
3. System install locations (`/usr/share/kicad/`, `~/bin/kicad/squashfs-root/usr/share/kicad/`, `/Applications/KiCad/...`, `C:\Program Files\KiCad\10.0\share\kicad\`, etc.).

If any of those resolve, your YAML can reference a stock symbol just by `library_nickname:symbol_name`:

```yaml
- ref: J1
  symbol: Connector:USB_C_Receptacle_USB2.0_14P
  footprint: Connector_USB:USB_C_Receptacle_GCT_USB4085
```

No `libraries:` / `footprint_libs:` entry needed for stock parts. Use those YAML keys only for project-local `.kicad_sym` / `.pretty` you author or vendor in.

## Repo layout

```
SchGen/
├── README.md                — this file
├── SKILL.md                 — full YAML schema + diagnostics reference (Claude skill format; symlink into consumers)
├── DESIGN.md                — rationale behind the major design decisions
├── LICENSE                  — Apache-2.0
├── Schgen.sln               — VS / Rider solution
├── src/
│   ├── Schgen.Cli/          — CLI entrypoint + commands (build/validate/symbols)
│   └── Schgen.Core/         — YAML model + KiCad emitters + placer
├── tests/
│   └── Schgen.Tests/        — xUnit suite (87 tests)
├── examples/
│   ├── minimal.yaml         — smallest single-sheet exerciser
│   ├── template_params.yaml — one template, four instantiations
│   └── multi_file/          — `includes:` splitting a board across files
└── scripts/
    └── install-libs.sh      — compat shim; forwards to `schgen install-stock-libs`
```

## Running tests

```bash
dotnet test
```

## License

Apache-2.0. See [LICENSE](LICENSE).

## Provenance

SchGen was extracted from the MyriadArc cartridge-console project in May 2026 where it generated five production board YAMLs (cart-pcb, cart-programmer, cart-row-pcb, console-board, console-programmer). The full reference docs (YAML schema, label-rendering rules, cap-proximity rules, diagnostics reference) live in [SKILL.md](SKILL.md). The rationale behind the major design decisions (why YAML, why source-verbatim symbol embedding, why net-naming as cluster signal, what schgen explicitly does not do) lives in [DESIGN.md](DESIGN.md).
