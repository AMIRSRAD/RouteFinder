"""Download the approved portable Ghidra release using validated HTTP ranges."""
import concurrent.futures, hashlib, json, pathlib, urllib.request, zipfile

ROOT = pathlib.Path(__file__).resolve().parent
URL = 'https://github.com/NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip'
SIZE = 569649598
PARTS = 12

def part(i):
    start, stop = SIZE * i // PARTS, SIZE * (i + 1) // PARTS - 1
    path = ROOT / ('ghidra-part-%02d.bin' % i)
    if path.exists() and path.stat().st_size == stop - start + 1:
        return path
    request = urllib.request.Request(URL, headers={'Range': 'bytes=%d-%d' % (start, stop)})
    with urllib.request.urlopen(request, timeout=90) as response, path.open('wb') as output:
        if response.status != 206 or response.headers.get('Content-Range') != 'bytes %d-%d/%d' % (start, stop, SIZE):
            raise RuntimeError('Server did not honor exact range')
        while chunk := response.read(1024 * 1024):
            output.write(chunk)
    if path.stat().st_size != stop - start + 1:
        raise RuntimeError('Incomplete range')
    print('Part complete', i, flush=True)
    return path

if __name__ == '__main__':
    with concurrent.futures.ThreadPoolExecutor(max_workers=PARTS) as workers:
        paths = list(workers.map(part, range(PARTS)))
    target = ROOT / 'ghidra_12.1.4_verified.zip'
    digest = hashlib.sha256()
    with target.open('wb') as output:
        for path in paths:
            with path.open('rb') as source:
                while chunk := source.read(1024 * 1024):
                    output.write(chunk)
                    digest.update(chunk)
    with urllib.request.urlopen('https://api.github.com/repos/NationalSecurityAgency/ghidra/releases/tags/Ghidra_12.1.4_build') as response:
        release = json.load(response)
    asset = next(x for x in release['assets'] if x['name'] == 'ghidra_12.1.4_PUBLIC_20260921.zip')
    expected = asset.get('digest')
    observed = 'sha256:' + digest.hexdigest()
    if expected and expected != observed:
        raise RuntimeError('Release checksum mismatch')
    (ROOT / 'ghidra-download.json').write_text(json.dumps({'source': URL, 'bytes': SIZE, 'sha256': digest.hexdigest(), 'upstream_digest': expected}, indent=2))
    with zipfile.ZipFile(target) as archive:
        for name in archive.namelist():
            if not (ROOT / name).resolve().is_relative_to(ROOT):
                raise RuntimeError('Unsafe archive member')
        archive.extractall(ROOT)
    print('Verified and extracted:', observed, flush=True)
