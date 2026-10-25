#!/usr/bin/env python3
"""Validate a tag version before it becomes a NuGet package or shell argument."""
import os
import re
import subprocess
import sys


def validate_version(version):
    # Use the canonical NuGet SemVer subset; exclude metadata and leading zeros
    # so the tag, package identity and artifact filename agree exactly.
    number = r'(?:0|[1-9][0-9]*)'
    identifier = r'(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    pattern = rf'{number}\.{number}\.{number}(?:-{identifier}(?:\.{identifier})*)?'
    if not re.fullmatch(pattern, version, flags=re.ASCII):
        raise ValueError(f'Invalid release version: {version!r}; expected e.g. 0.1.0-preview.1 or 0.1.0')
    return version


def main():
    ref = os.environ['GITHUB_REF']
    if os.environ['GITHUB_EVENT_NAME'] == 'push':
        if not ref.startswith('refs/tags/v'):
            raise ValueError('Publishing requires a v-prefixed version tag')
        version = validate_version(ref.removeprefix('refs/tags/v'))
        # Only release commits that have landed on main.
        subprocess.run(['git', 'merge-base', '--is-ancestor', 'HEAD', 'origin/main'], check=True)
    else:
        version = validate_version(os.environ['MANUAL_VERSION'])
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        output.write(f'version={version}\nprerelease={str("-" in version).lower()}\n')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        for valid in ['0.1.0', '0.1.0-preview.1', '1.2.3-rc.2', '1.0.0-beta-1']:
            assert validate_version(valid) == valid
        for invalid in ['', 'v0.1.0', '01.2.3', '1.2', '1.2.3-01', '1.2.3+build', '1.2.3\n', '1.2.3;echo bad']:
            try:
                validate_version(invalid)
            except ValueError:
                continue
            raise AssertionError(f'Accepted invalid version {invalid!r}')
        print('Release version checks passed.')
    else:
        main()
