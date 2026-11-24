#!/usr/bin/env python3
"""Validate DropLink hardware policy files without touching any device.

The policy is data, not code: a typo in a USB ID or an unknown status must fail
before the app trusts a device. This check is intentionally read-only and works
on Linux, macOS and Windows.
"""
import argparse
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT = ROOT / 'tools' / 'portable' / 'data' / 'hardware.default.json'
HW_RE = re.compile(r'^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&MI_[0-9A-F]{2})?$', re.I)
VID_PID_RE = re.compile(r'^[0-9A-F]{4}:[0-9A-F]{4}$', re.I)
STATUSES = {'verified', 'upstream_only', 'experimental', 'unsupported'}


def load(path):
    try:
        value = json.loads(path.read_text(encoding='utf-8'))
    except OSError as error:
        return None, [f'{path}: cannot read: {error}']
    except ValueError as error:
        return None, [f'{path}: invalid JSON: {error}']
    if not isinstance(value, dict):
        return None, [f'{path}: top level must be an object']
    return value, []


def validate(path):
    data, errors = load(path)
    if data is None:
        return errors, []
    warnings = []
    allow = data.get('allow_untested')
    if not isinstance(allow, bool):
        errors.append('allow_untested must be true or false')
    devices = data.get('devices')
    if not isinstance(devices, list) or not devices:
        errors.append('devices must be a non-empty array')
        return errors, warnings

    seen_hw = set()
    seen_usb = set()
    counts = {}
    for index, device in enumerate(devices):
        label = f'devices[{index}]'
        if not isinstance(device, dict):
            errors.append(f'{label}: must be an object')
            continue
        name = device.get('name')
        if not isinstance(name, str) or not name.strip():
            errors.append(f'{label}: name is required')
        hardware_id = device.get('hardware_id')
        match = HW_RE.match(hardware_id) if isinstance(hardware_id, str) else None
        if not match:
            errors.append(f'{label}: hardware_id must look like USB\\VID_0BDA&PID_C811')
        else:
            key = hardware_id.upper()
            if key in seen_hw:
                errors.append(f'{label}: duplicate hardware_id {hardware_id}')
            seen_hw.add(key)
        vid_pid = device.get('vid_pid')
        if not isinstance(vid_pid, str) or not VID_PID_RE.match(vid_pid):
            errors.append(f'{label}: vid_pid must look like 0BDA:C811')
        elif match and vid_pid.upper() != f'{match.group(1)}:{match.group(2)}'.upper():
            errors.append(f'{label}: vid_pid does not match hardware_id')
        if isinstance(vid_pid, str):
            key = vid_pid.upper()
            if key in seen_usb:
                warnings.append(f'{label}: duplicate vid_pid {vid_pid}; check the interface/MI field')
            seen_usb.add(key)
        status = device.get('status')
        if status not in STATUSES:
            errors.append(f'{label}: status must be one of {", ".join(sorted(STATUSES))}')
        else:
            counts[status] = counts.get(status, 0) + 1
        service = device.get('driver_service')
        if not isinstance(service, str) or not service.strip():
            errors.append(f'{label}: driver_service is required')
        for field in ('windows_builds', 'channels'):
            value = device.get(field, [])
            if not isinstance(value, list):
                errors.append(f'{label}: {field} must be an array')
        evidence = device.get('evidence')
        if evidence:
            if not isinstance(evidence, str):
                errors.append(f'{label}: evidence must be a path string')
            elif not (ROOT / evidence).exists():
                warnings.append(f'{label}: evidence path does not exist: {evidence}')
        if status == 'verified' and not evidence:
            warnings.append(f'{label}: verified device has no evidence path')

    if allow is False and counts.get('verified', 0) == 0:
        errors.append('allow_untested=false but no verified device is listed')
    return errors, warnings, counts, len(devices)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('files', nargs='*', type=pathlib.Path,
                        help='hardware JSON files; defaults to the portable template')
    args = parser.parse_args()
    files = args.files or [DEFAULT]
    failed = False
    for path in files:
        result = validate(path)
        if len(result) == 2:
            errors, warnings = result
            counts, total = {}, 0
        else:
            errors, warnings, counts, total = result
        for warning in warnings:
            print(f'WARN: {path}: {warning}')
        if errors:
            failed = True
            for error in errors:
                print(f'FAIL: {path}: {error}')
            continue
        detail = ', '.join(f'{key}={value}' for key, value in sorted(counts.items()))
        print(f'PASS: {path}: {total} device(s); {detail}')
    if failed:
        return 1
    print(f'PASS: {len(files)} hardware policy file(s)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
