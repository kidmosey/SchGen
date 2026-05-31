#!/usr/bin/env bash
# Populate tools/schgen/libs/{symbols,footprints} from a local KiCad install.
#
# Tries (in order):
#   1. Already-extracted AppImage at ~/bin/kicad/squashfs-root/usr/share/kicad/
#   2. System packages: /usr/share/kicad/, /usr/local/share/kicad/
#   3. macOS bundle: /Applications/KiCad/KiCad.app/Contents/SharedSupport/
#   4. AppImage at ~/bin/kicad/*.AppImage — extract via --appimage-extract
#
# Idempotent. Skips if libs/ already populated.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIBS_DIR="$SCRIPT_DIR/../libs"
SYMS_DST="$LIBS_DIR/symbols"
FPS_DST="$LIBS_DIR/footprints"

if [[ -d "$SYMS_DST" && -d "$FPS_DST" \
      && -n "$(ls -A "$SYMS_DST" 2>/dev/null)" \
      && -n "$(ls -A "$FPS_DST" 2>/dev/null)" ]]; then
    echo "schgen: libs/ already populated at $LIBS_DIR"
    exit 0
fi

mkdir -p "$LIBS_DIR"

copy_from() {
    local src="$1"
    if [[ -d "$src/symbols" && -d "$src/footprints" ]]; then
        echo "schgen: copying from $src"
        rm -rf "$SYMS_DST" "$FPS_DST"
        cp -r "$src/symbols" "$SYMS_DST"
        cp -r "$src/footprints" "$FPS_DST"
        return 0
    fi
    return 1
}

# Try already-extracted locations first.
for candidate in \
    "$HOME/bin/kicad/squashfs-root/usr/share/kicad" \
    "$HOME/Applications/kicad/squashfs-root/usr/share/kicad" \
    "/usr/share/kicad" \
    "/usr/local/share/kicad" \
    "/Applications/KiCad/KiCad.app/Contents/SharedSupport"
do
    if copy_from "$candidate"; then
        echo "schgen: libs/ populated"
        exit 0
    fi
done

# Fall back to extracting an AppImage.
APPIMAGE=""
for d in "$HOME/bin/kicad" "$HOME/Applications" "$HOME/Downloads"; do
    [[ -d "$d" ]] || continue
    APPIMAGE="$(find "$d" -maxdepth 1 -iname "*kicad*.AppImage" -type f | head -n1)"
    [[ -n "$APPIMAGE" ]] && break
done

if [[ -z "$APPIMAGE" ]]; then
    cat >&2 <<EOF
schgen: could not find a KiCad install.

Tried:
  - ~/bin/kicad/squashfs-root/
  - /usr/share/kicad/
  - /Applications/KiCad/KiCad.app/...
  - ~/bin/kicad/*.AppImage, ~/Applications/*.AppImage, ~/Downloads/*.AppImage

Install KiCad (https://www.kicad.org/download/) and re-run this script,
or pass --lib <path> on the schgen command line.
EOF
    exit 1
fi

echo "schgen: extracting $APPIMAGE (one-time, ~30s)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
# --appimage-extract's filter argument is unreliable across AppImage tool
# versions; do a full extract (~30s) and then cherry-pick what we need.
(cd "$TMP" && "$APPIMAGE" --appimage-extract >/dev/null)

SRC="$TMP/squashfs-root/usr/share/kicad"
if [[ ! -d "$SRC/symbols" || ! -d "$SRC/footprints" ]]; then
    echo "schgen: extraction produced unexpected layout at $SRC" >&2
    exit 1
fi

rm -rf "$SYMS_DST" "$FPS_DST"
cp -r "$SRC/symbols" "$SYMS_DST"
cp -r "$SRC/footprints" "$FPS_DST"
echo "schgen: libs/ populated from AppImage"
