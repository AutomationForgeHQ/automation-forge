"""Refresh paid metadata without fetching private source or exposing asset URLs."""
import json
import urllib.request
from pathlib import Path
from datetime import datetime, timezone

URL = 'https://app.kovati.dev/api/paid-releases'

def fetch_paid():
    with urllib.request.urlopen(URL, timeout=30) as response:
        data = json.load(response)
    if data.get('schemaVersion') != 1 or not isinstance(data.get('releases'), list):
        raise ValueError('Invalid paid catalogue')
    grouped = {}
    for r in data['releases']:
        # Explicit allow-list: private object locations and credentials never travel.
        version = {k: r[k] for k in ('version', 'engine', 'platform', 'channel', 'sha256', 'size', 'releasedAt')}
        version.update(url=None, symbols=None, notes=None)
        grouped.setdefault(r['plugin'], []).append(version)
    return grouped

def refresh(manifest, paid):
    for p in manifest['plugins']:
        if p['distribution'] == 'paid':
            p['versions'] = sorted(paid.get(p['id'], []), key=lambda v: (v['engine'], v['releasedAt']), reverse=True)
    return manifest

if __name__ == '__main__':
    path = Path(__file__).resolve().parents[1] / 'manifest.json'
    original = json.loads(path.read_text(encoding='utf8'))
    updated = refresh(json.loads(json.dumps(original)), fetch_paid())
    if updated != original:
        updated['generatedAt'] = datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
        path.write_text(json.dumps(updated, indent=2) + '\n', encoding='utf8', newline='\n')
        print('Paid manifest updated')
    else:
        print('Paid manifest unchanged')
