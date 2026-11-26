"""Build an overlay for an existing compact Windows x64 DropLink installation."""
import argparse
from pathlib import Path
import tempfile
import zipfile


REPO = Path(__file__).resolve().parents[1]
TEMPLATE = REPO / "tools" / "portable"
NOTICES = REPO / "docs" / "third-party"


def build(package, destination, runtime_pack, desktop_pack):
    runtime = package / "runtime"
    if not (runtime / "backend" / "airbridge_radio.exe").is_file():
        raise ValueError("Expected a compact DropLink installation with its runtime folder")
    files = {
        "DropLink.exe": package / "DropLink.exe",
        "使用说明.txt": TEMPLATE / "使用说明.txt",
        "runtime/data/settings.default.json": TEMPLATE / "data" / "settings.default.json",
        "runtime/app/DropLink.ico": runtime / "app" / "DropLink.ico",
        "runtime/app/DropLink-toast.png": runtime / "app" / "DropLink-toast.png",
        "runtime/licenses/DotNet-runtime-LICENSE.txt": runtime_pack / "LICENSE.TXT",
        "runtime/licenses/DotNet-runtime-NOTICES.txt": runtime_pack / "THIRD-PARTY-NOTICES.TXT",
        "runtime/licenses/DotNet-desktop-LICENSE.txt": desktop_pack / "LICENSE",
    }
    for notice in NOTICES.glob("*.txt"):
        files["runtime/licenses/" + notice.name] = notice
    for script in (TEMPLATE / "backend" / "airdrop").iterdir():
        if script.suffix in {".py", ".ps1", ".vbs"}:
            files["runtime/backend/airdrop/" + script.name] = script
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=destination.parent) as temp:
        archive = Path(temp) / "update.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as out:
            for name, path in sorted(files.items()):
                out.write(path, name)
        archive.replace(destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for argument in ("package", "destination", "runtime_pack", "desktop_pack"):
        parser.add_argument(argument, type=Path)
    args = parser.parse_args()
    build(args.package, args.destination, args.runtime_pack, args.desktop_pack)
