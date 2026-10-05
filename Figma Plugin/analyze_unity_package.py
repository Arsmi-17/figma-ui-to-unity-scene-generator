import json
import base64
import struct
import sys
from pathlib import Path


def png_size(encoded: str):
    try:
        data = base64.b64decode(encoded)
    except Exception:
        return None

    if len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n":
        return None

    return struct.unpack(">II", data[16:24])


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python analyze_unity_package.py <figma-unity-package.json>")
        return 2

    path = Path(sys.argv[1])
    package = json.loads(path.read_text(encoding="utf-8"))
    screens = package.get("screens") or [{"name": package.get("name", "Package"), "nodes": package.get("nodes", []), **package.get("root", {})}]
    assets = package.get("assets", {})
    unique_payloads = len(set(assets.values()))

    print(f"Package: {path}")
    print(f"Version: {package.get('version')}  Screens: {len(screens)}  Assets: {len(assets)}  Unique PNGs: {unique_payloads}")
    if len(assets) != unique_payloads:
        print(f"Warning: package contains {len(assets) - unique_payloads} duplicate encoded assets.")

    for index, screen in enumerate(screens, 1):
        width = float(screen.get("width") or 1)
        height = float(screen.get("height") or 1)
        screen_area = max(1.0, width * height)
        nodes = screen.get("nodes") or []
        counts = {}
        suspicious = []
        size_mismatches = []

        for node in nodes:
            kind = node.get("kind", "unknown")
            counts[kind] = counts.get(kind, 0) + 1
            area = float(node.get("width") or 0) * float(node.get("height") or 0)
            ratio = area / screen_area
            if kind in {"button", "input-field", "logo", "background", "panel-background"} and ratio > 0.12:
                suspicious.append((ratio, kind, node.get("name"), node.get("width"), node.get("height")))

            asset_path = node.get("asset")
            if asset_path and asset_path in assets:
                size = png_size(assets[asset_path])
                if size:
                    png_width, png_height = size
                    node_width = float(node.get("width") or 0)
                    node_height = float(node.get("height") or 0)
                    if abs(png_width - node_width) > 2 or abs(png_height - node_height) > 2:
                        size_mismatches.append((kind, node.get("name"), node_width, node_height, png_width, png_height))

        print(f"\nScreen {index}: {screen.get('name')}  {width:g}x{height:g}")
        print(f"  Nodes: {len(nodes)}  Kinds: {counts}")
        if screen.get("exportedAssetCount") is not None:
            print(f"  Exported assets for screen: {screen.get('exportedAssetCount')}/{screen.get('exportLimit')}")
        if screen.get("truncated"):
            print("  Warning: this screen hit the per-screen export limit; some objects may be missing.")
        if counts.get("screen-background", 0) != 1:
            print("  Warning: expected exactly one screen-background raster for vector/background alignment.")
        if suspicious:
            print("  Suspicious large semantic nodes:")
            for ratio, kind, name, node_width, node_height in sorted(suspicious, reverse=True):
                print(f"    {kind:12} {ratio:6.1%} {node_width}x{node_height}  {name}")
        if size_mismatches:
            print("  PNG size differs from JSON box; Unity may need render-bound placement for these:")
            for kind, name, node_width, node_height, png_width, png_height in size_mismatches[:20]:
                print(f"    {kind:18} json={node_width:g}x{node_height:g} png={png_width}x{png_height}  {name}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
