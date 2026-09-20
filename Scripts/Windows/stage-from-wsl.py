#!/usr/bin/env python3
"""Copy source to a Windows local build folder; never copy user authentication."""
import argparse
import json
import pathlib
import shutil

parser = argparse.ArgumentParser()
parser.add_argument('destination', type=pathlib.Path)
args = parser.parse_args()
source = pathlib.Path(__file__).resolve().parents[2]
destination = args.destination.resolve()
if destination == source or source in destination.parents or destination in source.parents:
    parser.error('Destination must be outside the source checkout.')
managed = ('Windows', 'Support', 'Scripts', 'Resources', 'Tests')
marker = destination / '.codex-syncbar-build-stage.json'
if marker.exists():
    try:
        previous = json.loads(marker.read_text())
        if previous != {'source': str(source), 'managed': list(managed)}:
            parser.error('Destination belongs to a different source checkout.')
    except (OSError, ValueError):
        parser.error('Destination has an invalid build-stage marker.')
elif destination.exists() and any(destination.iterdir()):
    parser.error('Destination is not an empty build stage. Choose an empty directory.')
destination.mkdir(parents=True, exist_ok=True)
marker.write_text(json.dumps({'source': str(source), 'managed': list(managed)}, indent=2) + '\n')
ignore = shutil.ignore_patterns('bin', 'obj', '.vs', 'AppPackages', '*.pfx', '*.cer')


def reconcile(source_dir, target_dir):
    """Remove stale source within owned subtrees while retaining build caches."""
    if not target_dir.exists():
        return
    children = list(target_dir.iterdir())
    ignored = ignore(str(target_dir), [child.name for child in children])
    for child in children:
        if child.name in ignored:
            continue
        expected = source_dir / child.name
        if child.is_symlink() or not expected.exists() or child.is_dir() != expected.is_dir():
            if child.is_dir() and not child.is_symlink():
                shutil.rmtree(child)
            else:
                child.unlink()
        elif child.is_dir():
            reconcile(expected, child)


for name in managed:
    reconcile(source / name, destination / name)
    shutil.copytree(source / name, destination / name, dirs_exist_ok=True, ignore=ignore)
print(destination)
