"""Verify this source tree and optionally its complete retained data asset."""
from pathlib import Path
import argparse
import csv
import hashlib
import json


def sha256(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    failures = []
    with (root / 'FILES_SHA256.csv').open(encoding='utf-8-sig', newline='') as stream:
        rows = list(csv.DictReader(stream))
    for row in rows:
        p = (root / row['path']).resolve()
        if not p.is_relative_to(root) or not p.is_file():
            failures.append(row['path'] + ': missing or invalid path')
        elif p.stat().st_size != int(row['bytes']) or sha256(p) != row['sha256']:
            failures.append(row['path'] + ': byte/hash mismatch')
    release_checked = False
    if args.release is not None:
        expected = json.loads((root / 'RELEASE_ASSET.json').read_text(encoding='utf-8'))
        if not args.release.is_file():
            failures.append('Complete Release asset: file missing')
        elif args.release.stat().st_size != expected['bytes'] or sha256(args.release) != expected['sha256']:
            failures.append('Complete Release asset: byte/hash mismatch')
        else:
            release_checked = True
    print(json.dumps({'status': 'FAIL' if failures else 'PASS',
                      'files_checked': len(rows), 'complete_release_checked': release_checked,
                      'failures': failures}, indent=2))
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
