"""Copy the explicitly selected production files into a separate public checkout."""

from pathlib import Path
import argparse
import hashlib
import shutil
import subprocess


def synchronize(source: Path, destination: Path) -> None:
    source, destination = source.resolve(), destination.resolve()
    if source == destination or source in destination.parents or destination in source.parents:
        raise ValueError("Source and public checkout must be separate directories")
    if not (destination / ".git").exists():
        raise ValueError("Initialize the public Git checkout before synchronizing")
    selected: set[Path] = set()
    for pattern in (source / "public-files.txt").read_text(encoding="utf-8").splitlines():
        if not pattern.strip() or pattern.startswith("#"):
            continue
        if Path(pattern).is_absolute() or ".." in Path(pattern).parts:
            raise ValueError(f"Invalid production pattern: {pattern}")
        matches = [path for path in source.glob(pattern) if path.is_file()]
        if not matches:
            raise ValueError(f"Production pattern has no files: {pattern}")
        for path in matches:
            if path.is_symlink() or any(part in {".git", "bin", "obj", "dist"} for part in path.relative_to(source).parts):
                raise ValueError(f"Production pattern selected an excluded file: {path.name}")
            selected.add(path.relative_to(source))
    tracked = subprocess.run(["git", "-C", str(destination), "ls-files", "-z"], check=True, capture_output=True).stdout
    unexpected = {Path(path.decode("utf-8")) for path in tracked.split(b"\0") if path} - selected
    if unexpected:
        raise ValueError("Public checkout contains files outside the production list; review them before synchronizing: " + ", ".join(map(str, sorted(unexpected))))
    for relative in sorted(selected):
        target = destination / relative
        if target.is_symlink() or any(parent.is_symlink() for parent in target.parents if parent != destination.parent):
            raise ValueError(f"Public destination contains a symlink: {relative}")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source / relative, target)
        if hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256((source / relative).read_bytes()).digest():
            raise OSError(f"Production copy differs: {relative}")
    print(f"Synchronized {len(selected)} production files; Git history and remotes were not changed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("public_checkout", type=Path)
    arguments = parser.parse_args()
    synchronize(Path(__file__).resolve().parents[1], arguments.public_checkout)
