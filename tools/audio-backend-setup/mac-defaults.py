#!/usr/bin/env python3
"""CoreAudio UID snapshot/restore; default is read-only. No input/audio stream is opened."""
import argparse
import ctypes as c
import datetime
import hashlib
import json
import os
import pathlib
import platform
import stat
import sys

def four(text): return int.from_bytes(text.encode('ascii'), 'big')
class Address(c.Structure):
    _fields_ = [('selector', c.c_uint32), ('scope', c.c_uint32), ('element', c.c_uint32)]
def address(selector, scope='glob'): return Address(four(selector), four(scope), 0)
audio = None
cf = None
def check(status):
    if status: raise RuntimeError('CoreAudio OSStatus=%d' % status)
def data(device, selector, scope='glob'):
    addr, size = address(selector, scope), c.c_uint32()
    check(audio.AudioObjectGetPropertyDataSize(device, c.byref(addr), 0, None, c.byref(size)))
    if size.value > 65536: raise RuntimeError('Unexpected property size')
    buf = c.create_string_buffer(size.value)
    check(audio.AudioObjectGetPropertyData(device, c.byref(addr), 0, None, c.byref(size), buf))
    return buf.raw[:size.value]
def text(device, selector):
    raw = data(device, selector)
    if len(raw) != c.sizeof(c.c_void_p): raise RuntimeError('Invalid CFString property size')
    ref = c.c_void_p(int.from_bytes(raw, sys.byteorder))
    if not ref.value: return ''
    buf = c.create_string_buffer(8192)
    try:
        if not cf.CFStringGetCString(ref, buf, len(buf), 0x08000100): raise RuntimeError('Invalid CFString encoding')
        return buf.value.decode('utf-8')
    finally: cf.CFRelease(ref)
def channels(device, scope):
    raw = data(device, 'slay', scope)
    if len(raw) < 8: raise RuntimeError('Invalid AudioBufferList')
    count = int.from_bytes(raw[:4], sys.byteorder)
    if 8 + count * 16 > len(raw): raise RuntimeError('Invalid AudioBufferList bounds')
    return sum(int.from_bytes(raw[8 + i*16:12 + i*16], sys.byteorder) for i in range(count))
def inventory():
    ids = data(1, 'dev#')
    if len(ids) % 4: raise RuntimeError('Invalid device list')
    devices = []
    for pos in range(0, len(ids), 4):
        device = int.from_bytes(ids[pos:pos+4], sys.byteorder)
        devices.append(dict(id=device, uid=text(device, 'uid '), name=text(device, 'lnam'),
                            inputChannels=channels(device, 'inpt'), outputChannels=channels(device, 'outp')))
    defaults = []
    for role, selector in [('input', 'dIn '), ('output', 'dOut'), ('systemOutput', 'sOut')]:
        device = int.from_bytes(data(1, selector), sys.byteorder)
        match = next((d for d in devices if d['id'] == device), None)
        defaults.append(dict(role=role, uid=match['uid'] if match else None, name=match['name'] if match else None))
    preserved = []
    base = pathlib.Path('/Library/Audio/Plug-Ins/HAL/BlackHole2ch.driver')
    if base.exists():
        for path in [base, *sorted(base.rglob('*'))]:
            meta = path.lstat()
            record = dict(path=str(path), mode=oct(stat.S_IMODE(meta.st_mode)), owner=meta.st_uid, group=meta.st_gid)
            if path.is_file() and not path.is_symlink(): record['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
            preserved.append(record)
    return dict(schema=1, platform='Mac', machine=platform.node(), identity=os.getuid(),
                capturedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(), defaults=defaults,
                devices=devices, preservedBlackHole2ch=preserved)
def main():
    global audio, cf
    if sys.platform != 'darwin': raise RuntimeError('Mac only')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['snapshot', 'restore'])
    parser.add_argument('file', type=pathlib.Path)
    parser.add_argument('--apply', action='store_true', help='Explicitly restore the three default UIDs')
    args = parser.parse_args()
    audio = c.CDLL('/System/Library/Frameworks/CoreAudio.framework/CoreAudio')
    cf = c.CDLL('/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation')
    audio.AudioObjectGetPropertyDataSize.argtypes = [c.c_uint32, c.POINTER(Address), c.c_uint32, c.c_void_p, c.POINTER(c.c_uint32)]
    audio.AudioObjectGetPropertyData.argtypes = [c.c_uint32, c.POINTER(Address), c.c_uint32, c.c_void_p, c.POINTER(c.c_uint32), c.c_void_p]
    audio.AudioObjectSetPropertyData.argtypes = [c.c_uint32, c.POINTER(Address), c.c_uint32, c.c_void_p, c.c_uint32, c.c_void_p]
    cf.CFStringGetCString.argtypes = [c.c_void_p, c.c_void_p, c.c_long, c.c_uint32]
    cf.CFStringGetCString.restype = c.c_bool
    cf.CFRelease.argtypes = [c.c_void_p]
    current = inventory()
    if args.mode == 'snapshot':
        if args.apply: raise RuntimeError('--apply is only valid with restore')
        args.file.parent.mkdir(parents=True, exist_ok=True)
        with args.file.open('x') as f: json.dump(current, f, ensure_ascii=False, indent=2)
        print(json.dumps(current, ensure_ascii=False, indent=2))
        return
    saved = json.loads(args.file.read_text())
    for key in ['schema', 'platform', 'machine', 'identity']:
        if saved.get(key) != current[key]: raise RuntimeError('Snapshot belongs to different machine/user/schema')
    targets = saved.get('defaults', [])
    if len(targets) != 3 or {d['role'] for d in targets} != {'input', 'output', 'systemOutput'}:
        raise RuntimeError('Exactly three distinct roles required')
    plan = []
    for target in targets:
        matches = [d for d in current['devices'] if d['uid'] == target['uid'] and d['name'] == target['name']]
        if len(matches) != 1: raise RuntimeError('Saved device UID unavailable/renamed; no fallback chosen: %s' % target['name'])
        endpoint = matches[0]
        field = 'inputChannels' if target['role'] == 'input' else 'outputChannels'
        if endpoint[field] <= 0: raise RuntimeError('Saved UID does not support role')
        actual = next(d for d in current['defaults'] if d['role'] == target['role'])
        changed = actual['uid'] != target['uid']
        print('%s: %s => %s; changed=%s' % (target['role'], actual['name'], target['name'], changed))
        if changed: plan.append((target, endpoint))
    if not args.apply:
        print('DRY RUN: %d changed roles; no defaults written.' % len(plan))
        return
    for target, endpoint in plan:
        selector = {'input': 'dIn ', 'output': 'dOut', 'systemOutput': 'sOut'}[target['role']]
        addr, device = address(selector), c.c_uint32(endpoint['id'])
        check(audio.AudioObjectSetPropertyData(1, c.byref(addr), 0, None, c.sizeof(device), c.byref(device)))
    after = inventory()
    if any(next(d['uid'] for d in after['defaults'] if d['role'] == target['role']) != target['uid'] for target in targets):
        raise RuntimeError('Default UID restore verification failed')
    print('All three default UIDs match snapshot.')
if __name__ == '__main__':
    try: main()
    except Exception as error:
        print('FAILED: %s' % error, file=sys.stderr)
        sys.exit(1)
