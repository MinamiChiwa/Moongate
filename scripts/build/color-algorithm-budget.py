"""以固定 Git 源码和同一合成样本比较原算法与 Worker 算法资源。"""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import importlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import time
import types

REPOSITORY = Path(__file__).resolve().parents[2]
COMMIT = '5e53ec8481857563c6718b282c8990ea3830931d'


def memory():
    class Counters(ctypes.Structure):
        _fields_ = [('cb', wintypes.DWORD), ('faults', wintypes.DWORD)] + [(name, ctypes.c_size_t) for name in
            ('peakWorkingSet', 'workingSet', 'peakPaged', 'paged', 'peakNonpaged', 'nonpaged', 'pagefile', 'peakPagefile', 'privateBytes')]
    values = Counters(); values.cb = ctypes.sizeof(values)
    current = ctypes.windll.kernel32.GetCurrentProcess; current.restype = wintypes.HANDLE
    query = ctypes.windll.psapi.GetProcessMemoryInfo
    query.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    if not query(current(), ctypes.byref(values), values.cb): raise ctypes.WinError()
    return dict(workingSetBytes=values.workingSet, privateBytes=values.privateBytes, peakWorkingSetBytes=values.peakWorkingSet)


def source_code(name, source_root):
    path = f'work/studio/{name}.py'
    raw = subprocess.run(['git', '-C', str(source_root), 'show', f'{COMMIT}:{path}'], capture_output=True, check=True).stdout
    baseline = json.loads((REPOSITORY / 'scripts/maintenance/source-baselines.json').read_text('utf-8'))
    expected = next(row['sha256'] for source in baseline['sources'] if source['id'] == 'color-studio' for row in source['files'] if row['path'] == path)
    if hashlib.sha256(raw).hexdigest() != expected: raise ValueError(f'基线哈希不符：{path}')
    code = raw.decode('utf-8')
    modules = {value.stem for value in (REPOSITORY / 'workers/color-engine/mabitool_color/_vendor').glob('*.py')}
    code = re.sub(r'(?m)^(\s*)from (\w+) import ', lambda match:
        f'{match[1]}from mabitool_color._vendor.{match[2]} import ' if match[2] in modules else match[0], code)
    return code


def source_module(name, code):
    module = types.ModuleType('baseline_' + name)
    exec(compile(code, f'{COMMIT}:work/studio/{name}.py', 'exec'), module.__dict__)
    return module


def child(mode, source_root):
    sys.path.insert(0, str(REPOSITORY / 'workers/color-engine'))
    baseline_code = {name: source_code(name, source_root) for name in ('native_periodic_search', 'screenshot_search')} if mode == 'baseline' else {}
    before = memory(); import_started = time.perf_counter()
    import numpy as np
    import cv2
    cv2.setNumThreads(2)
    if mode == 'baseline':
        periodic = source_module('native_periodic_search', baseline_code['native_periodic_search'])
        screenshot = source_module('screenshot_search', baseline_code['screenshot_search'])
    else:
        periodic = importlib.import_module('mabitool_color._vendor.native_periodic_search')
        screenshot = importlib.import_module('mabitool_color._vendor.screenshot_search')
    imported = memory(); import_ms = (time.perf_counter() - import_started) * 1000
    rules = [dict(enabled=True, colors=['#202020', '#212121'], exact=index == 0, tolerance=8, priority=index + 1) for index in range(3)]
    evaluations = []
    for colors in (['#212121', '#202020'], ['#222222', '#202020'], ['#212121', None, '#202020'], ['#202020'] * 3):
        times = []
        for _ in range(100):
            start = time.perf_counter(); result = screenshot.actual_result(colors, rules[:len(colors)]); times.append((time.perf_counter() - start) * 1000)
        result = {key: value for key, value in result.items() if key != 'rank'}
        evaluations.append(dict(colors=colors, result=result, timingsMs=percentiles(times)))
    samples = []
    for size in (254, 512, 1536):
        pixels = np.arange(size * size * 3, dtype=np.uint32).astype(np.uint8).reshape(size, size, 3)
        session = dict(pixels=(pixels,), color_preserve_ratio=.25)
        start = time.perf_counter(); result = periodic.periodic_samples(session, 0, 0., 0.); cold = (time.perf_counter() - start) * 1000
        digest = hashlib.sha256(b''.join(array.tobytes() for array in result)).hexdigest()
        del result
        warm = []
        for _ in range(3):
            start = time.perf_counter(); again = periodic.periodic_samples(session, 0, 0., 0.); warm.append((time.perf_counter() - start) * 1000)
            if hashlib.sha256(b''.join(array.tobytes() for array in again)).hexdigest() != digest: raise ValueError('同样本结果漂移')
            del again
        samples.append(dict(size=size, resultSha256=digest, coldMs=cold, repeatedMs=percentiles(warm),
            cachedBytes=sum(sum(array.nbytes for array in row) for row in session['_periodic_sample_cache'].values()), memory=memory()))
        del pixels, session
    return dict(mode=mode, before=before, imported=imported, importMs=import_ms, evaluations=evaluations, preprocessing=samples, final=memory())


def percentiles(values):
    values = sorted(values)
    return dict(count=len(values), p50=values[(len(values) - 1) // 2], p95=values[min(len(values) - 1, int(len(values) * .95))], maximum=values[-1])


def package_bytes():
    files = set()
    versions = []
    for line in (REPOSITORY / 'workers/color-engine/requirements.txt').read_text('utf-8').splitlines():
        if not line: continue
        distribution = importlib.metadata.distribution(line.split('==')[0])
        versions.append(dict(requirement=line, installedVersion=distribution.version))
        files.update(Path(distribution.locate_file(file)).resolve() for file in distribution.files or () if Path(distribution.locate_file(file)).is_file())
    return sum(file.stat().st_size for file in files), len(files), versions


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--child', choices=['baseline', 'worker'])
    args = parser.parse_args()
    if args.child:
        print(json.dumps(child(args.child, args.source_root), ensure_ascii=False)); return
    if args.output is None or not args.output.resolve().is_relative_to(REPOSITORY / 'artifacts/reports'):
        raise ValueError('报告须位于 artifacts/reports')
    results = []
    environment = os.environ | {'PYTHONUTF8': '1'} | {name: '2' for name in ('OPENBLAS_NUM_THREADS', 'OMP_NUM_THREADS', 'MKL_NUM_THREADS', 'NUMEXPR_NUM_THREADS')}
    for mode in ('baseline', 'worker'):
        process = subprocess.run([sys.executable, '-B', '-s', str(Path(__file__).resolve()), '--source-root', str(args.source_root), '--child', mode],
            env=environment, capture_output=True, text=True, encoding='utf-8', check=True)
        results.append(json.loads(process.stdout))
    for old, new in zip(results[0]['evaluations'], results[1]['evaluations']):
        if old['result'] != new['result']: raise ValueError('原评价算法结果不一致')
    for old, new in zip(results[0]['preprocessing'], results[1]['preprocessing']):
        if old['resultSha256'] != new['resultSha256']: raise ValueError('原纹理采样结果不一致')
    installed, file_count, versions = package_bytes()
    report = dict(schemaVersion=1, measuredAt=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), python=sys.version,
        operatingSystem=platform.platform(), logicalProcessors=os.cpu_count(), sourceCommit=COMMIT, maximumParallelism=2,
        sample='4 组规则评价与 254/512/1536 方形合成纹理，原函数仅调整导入命名空间', equalResults=True,
        requiredPythonDistributionsInstalledBytes=installed, requiredPythonDistributionFiles=file_count, pythonDistributions=versions, results=results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), 'utf-8')
    print(args.output)


if __name__ == '__main__': main()
