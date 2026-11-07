"""Read-only installed-artifact manifest; does not load any target code."""
import hashlib, json, pathlib

root = pathlib.Path(r'C:\Program Files\EZConnect')
destination = pathlib.Path(__file__).resolve().parent.parent / 'ezconnect-research' / 'analysis' / 'installation-manifest.json'
entries = []
for path in sorted(root.rglob('*')):
    if path.is_file():
        digest = hashlib.sha256()
        with path.open('rb') as source:
            while chunk := source.read(1024 * 1024):
                digest.update(chunk)
        entries.append({'relative_path': str(path.relative_to(root)), 'bytes': path.stat().st_size, 'sha256': digest.hexdigest()})
destination.write_text(json.dumps({'evidence_type': 'ordinary read-only filesystem inventory, not REA', 'root': str(root), 'file_count': len(entries), 'total_bytes': sum(x['bytes'] for x in entries), 'files': entries}, indent=2))
print(json.dumps({'files': len(entries), 'bytes': sum(x['bytes'] for x in entries)}))
