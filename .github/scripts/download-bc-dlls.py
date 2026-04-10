#!/usr/bin/env python3
"""
Download specific BC Service Tier DLLs from a platform artifact ZIP using
HTTP Range requests. Only downloads the ~8 DLLs needed by AlRunner.csproj
instead of the full ~1.2GB artifact (~2-5MB vs ~211MB with all ServiceTier DLLs).

Usage:
  python3 download-bc-dlls.py <artifact_url> <total_size> <output_dir>
"""

import struct
import sys
import os
import subprocess
import zlib
import tempfile
import shutil

# Only the DLLs referenced in AlRunner.csproj
NEEDED_DLLS = {
    'microsoft.dynamics.nav.ncl.dll',
    'microsoft.dynamics.nav.types.dll',
    'microsoft.dynamics.nav.common.dll',
    'microsoft.dynamics.nav.language.dll',
    'microsoft.dynamics.nav.core.dll',
    'microsoft.dynamics.nav.types.report.dll',
    'microsoft.dynamics.nav.types.report.base.dll',
    'microsoft.dynamics.nav.types.report.runtime.dll',
}


def download(url, output_path, byte_range=None):
    cmd = ['curl', '-s', '-f', '-o', output_path]
    if byte_range:
        cmd.extend(['-r', byte_range])
    cmd.append(url)
    result = subprocess.run(cmd, capture_output=True)
    return result.returncode == 0


def parse_central_directory(data, cd_start, entry_count):
    entries = []
    pos = cd_start
    for _ in range(entry_count):
        if pos + 46 > len(data):
            break
        if data[pos:pos+4] != b'\x50\x4b\x01\x02':
            break
        comp_method  = struct.unpack_from('<H', data, pos + 10)[0]
        comp_size    = struct.unpack_from('<I', data, pos + 20)[0]
        uncomp_size  = struct.unpack_from('<I', data, pos + 24)[0]
        name_len     = struct.unpack_from('<H', data, pos + 28)[0]
        extra_len    = struct.unpack_from('<H', data, pos + 30)[0]
        comment_len  = struct.unpack_from('<H', data, pos + 32)[0]
        local_offset = struct.unpack_from('<I', data, pos + 42)[0]
        name = data[pos+46:pos+46+name_len].decode('utf-8', errors='replace')
        name = name.replace('\\', '/')
        entries.append({
            'name': name, 'comp_method': comp_method,
            'comp_size': comp_size, 'uncomp_size': uncomp_size,
            'offset': local_offset,
        })
        pos += 46 + name_len + extra_len + comment_len
    return entries


def main():
    if len(sys.argv) < 4:
        print("Usage: download-bc-dlls.py <artifact_url> <total_size> <output_dir>")
        sys.exit(1)

    url        = sys.argv[1]
    total_size = int(sys.argv[2])
    output_dir = sys.argv[3]
    os.makedirs(output_dir, exist_ok=True)

    print(f"Artifact: {url}")
    print(f"Size: {total_size:,} bytes ({total_size // 1048576} MB)")
    print(f"Target DLLs: {len(NEEDED_DLLS)}")

    tmp_dir = tempfile.mkdtemp(prefix='bc-dlls-')

    try:
        # Step 1: Download last 64KB to find EOCD
        tail_size  = 65536
        tail_start = total_size - tail_size
        tail_file  = os.path.join(tmp_dir, 'tail.bin')
        print("Downloading EOCD (64 KB)...")
        if not download(url, tail_file, f'{tail_start}-{total_size - 1}'):
            sys.exit(1)

        with open(tail_file, 'rb') as f:
            tail = f.read()

        eocd_pos = tail.rfind(b'\x50\x4b\x05\x06')
        if eocd_pos == -1:
            print("ERROR: EOCD not found")
            sys.exit(1)

        entry_count = struct.unpack_from('<H', tail, eocd_pos + 10)[0]
        cd_size     = struct.unpack_from('<I', tail, eocd_pos + 12)[0]
        cd_offset   = struct.unpack_from('<I', tail, eocd_pos + 16)[0]
        print(f"Central directory: {entry_count} entries, {cd_size // 1024} KB")

        # Step 2: Download central directory
        cd_start_in_tail = len(tail) - (total_size - cd_offset)
        if cd_start_in_tail < 0:
            print("Downloading central directory...")
            cd_file = os.path.join(tmp_dir, 'cd.bin')
            if not download(url, cd_file, f'{cd_offset}-{total_size - 1}'):
                sys.exit(1)
            with open(cd_file, 'rb') as f:
                tail = f.read()
            cd_start_in_tail = 0

        entries = parse_central_directory(tail, cd_start_in_tail, entry_count)
        print(f"Parsed {len(entries)} entries")

        # Step 3: Find only the DLLs we need from ServiceTier
        matching = []
        for e in entries:
            name_lower = e['name'].lower()
            if 'servicetier/' not in name_lower or '/service/' not in name_lower:
                continue
            # Only take DLLs directly in Service/, not in subdirectories
            after_service = name_lower.split('/service/')[-1]
            if '/' in after_service:
                continue  # skip management/, sideservices/, etc.
            basename = os.path.basename(name_lower)
            if basename in NEEDED_DLLS and e['comp_size'] > 0:
                matching.append(e)

        print(f"Found {len(matching)} of {len(NEEDED_DLLS)} needed DLLs")
        if len(matching) == 0:
            print("ERROR: No matching DLLs found")
            sys.exit(1)

        for e in matching:
            print(f"  {os.path.basename(e['name'])} ({e['comp_size'] // 1024} KB compressed)")

        # Step 4: Download each DLL individually via range request
        # Each file needs: local header (30 bytes) + name + extra + compressed data
        # We add a generous buffer for the local header overhead
        extracted = 0
        total_bytes = 0
        for e in matching:
            basename = os.path.basename(e['name'])
            # Range: from local file header to end of compressed data
            # Local header is 30 + name_len + extra_len bytes, then compressed data follows
            # We don't know extra_len upfront, so download with generous extra (512 bytes)
            range_start = e['offset']
            range_end   = e['offset'] + 30 + len(e['name'].encode('utf-8')) + 512 + e['comp_size']
            range_end   = min(range_end, total_size - 1)

            range_file = os.path.join(tmp_dir, f'{basename}.bin')
            if not download(url, range_file, f'{range_start}-{range_end}'):
                print(f"  WARNING: Failed to download {basename}")
                continue

            with open(range_file, 'rb') as f:
                data = f.read()

            # Parse local file header
            if data[:4] != b'\x50\x4b\x03\x04':
                print(f"  WARNING: Invalid local header for {basename}")
                continue

            name_len   = struct.unpack_from('<H', data, 26)[0]
            extra_len  = struct.unpack_from('<H', data, 28)[0]
            data_start = 30 + name_len + extra_len

            if data_start + e['comp_size'] > len(data):
                print(f"  WARNING: {basename} data truncated, re-downloading with larger range")
                range_end = e['offset'] + data_start + e['comp_size'] + 16
                if not download(url, range_file, f'{range_start}-{range_end}'):
                    continue
                with open(range_file, 'rb') as f:
                    data = f.read()
                name_len   = struct.unpack_from('<H', data, 26)[0]
                extra_len  = struct.unpack_from('<H', data, 28)[0]
                data_start = 30 + name_len + extra_len

            comp_data = data[data_start:data_start + e['comp_size']]

            if e['comp_method'] == 0:
                file_data = comp_data
            elif e['comp_method'] == 8:
                try:
                    file_data = zlib.decompress(comp_data, -15)
                except zlib.error as err:
                    print(f"  WARNING: Decompression failed for {basename}: {err}")
                    continue
            else:
                print(f"  WARNING: Unsupported compression for {basename}")
                continue

            out_path = os.path.join(output_dir, basename)
            with open(out_path, 'wb') as f:
                f.write(file_data)

            extracted   += 1
            total_bytes += len(file_data)
            os.remove(range_file)

        print(f"\nExtracted {extracted} DLLs ({total_bytes // 1024} KB)")

        if extracted < len(NEEDED_DLLS):
            found = {os.path.basename(e['name']).lower() for e in matching}
            missing = NEEDED_DLLS - found
            if missing:
                print(f"WARNING: Missing DLLs: {', '.join(sorted(missing))}")

    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


if __name__ == '__main__':
    main()
