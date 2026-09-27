"""Collect published dependency license files without vendoring library code."""
import json
import hashlib
from pathlib import Path
import subprocess
import urllib.request
import urllib.error

root = Path(__file__).resolve().parents[1]
metadata = json.loads(subprocess.check_output(['cargo', 'metadata', '--manifest-path', str(root / 'native/Cargo.toml'),
                                              '--locked', '--format-version', '1']))
resolved = set()
for platform in ['x86_64-pc-windows-msvc', 'i686-pc-windows-msvc', 'aarch64-pc-windows-msvc',
                 'x86_64-unknown-linux-musl', 'aarch64-unknown-linux-musl', 'x86_64-unknown-linux-gnu']:
    graph = json.loads(subprocess.check_output(['cargo', 'metadata', '--manifest-path', str(root / 'native/Cargo.toml'),
        '--locked', '--format-version', '1', '--filter-platform', platform]))
    resolved.update(node['id'] for node in graph['resolve']['nodes'])
output = ['Rust dependency notices\nGenerated from native/Cargo.lock; do not edit by hand.\n']
texts = {}
missing = []
count = 0
for package in sorted(metadata['packages'], key=lambda p: (p['name'], p['version'])):
    if package['source'] is None or package['id'] not in resolved:
        continue
    count += 1
    directory = Path(package['manifest_path']).parent
    files = sorted(p for p in directory.iterdir() if p.is_file() and
                   p.name.lower().startswith(('license', 'licence', 'copying', 'notice', 'unlicense')))
    if package.get('license_file'):
        specific = directory / package['license_file']
        if specific not in files:
            files.append(specific)
    # Some workspace crates omit their repository-root license from the crate
    # archive. Fetch only the exact published VCS revision, then cache it for
    # offline regeneration and include its provenance in the notice.
    supplemental = root / 'licenses/rust-supplemental' / (package['name'] + '-' + package['version'])
    if not files and supplemental.is_dir():
        files = sorted(p for p in supplemental.iterdir() if p.is_file() and p.name != 'SOURCE.txt')
    if not files:
        vcs = json.loads((directory / '.cargo_vcs_info.json').read_text())
        revision = vcs['git']['sha1']
        repository = package['repository'].removesuffix('.git').replace('https://github.com/', 'https://raw.githubusercontent.com/')
        supplemental.mkdir(parents=True, exist_ok=True)
        for name in ['LICENSE', 'LICENSE.txt', 'LICENSE-APACHE', 'LICENSE-MIT', 'COPYING', 'COPYING.MIT', 'COPYING.Apache-2.0']:
            target = supplemental / name
            if not target.exists():
                url = repository + '/' + revision + '/' + name
                try:
                    target.write_bytes(urllib.request.urlopen(url, timeout=20).read())
                except urllib.error.HTTPError as error:
                    if error.code != 404:
                        raise
                    continue
            files.append(target)
        (supplemental / 'SOURCE.txt').write_text(package['repository'] + '/tree/' + revision + '\n')
    if not files:
        missing.append(package['name'])
    output.append(f"\n{'=' * 72}\n{package['name']} {package['version']}\nLicense: {package.get('license')}\nSource: {package.get('repository') or package['source']}\n")
    for file in files:
        text = '\n'.join(line.rstrip() for line in file.read_text(encoding='utf-8').splitlines()).strip() + '\n'
        reference = hashlib.sha256(text.encode()).hexdigest()[:16]
        texts[reference] = text
        output.append(f'{file.name}: full text [{reference}]')
if missing:
    raise RuntimeError('Review packages without license files: ' + ', '.join(missing))
for reference, text in texts.items():
    output.append(f'\n=== Full license text [{reference}] ===\n' + text)
(root / 'licenses/RUST-THIRD-PARTY-NOTICES.txt').write_text(
    '\n'.join(line.rstrip() for line in '\n'.join(output).splitlines()) + '\n', encoding='utf-8')
print('Collected license files for', count, 'dependencies')
