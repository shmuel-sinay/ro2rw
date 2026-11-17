"""
MTK DA (Download Agent) blob parser.
Parses MTK_DA_V5.bin / DA_PL.bin to find chip-specific entries.

Binary format (from mtkclient analysis):
  Offset 0x68: count_da (4 bytes LE)
  Offset 0x6C: DA entries, each 0xDC bytes (new) or 0xD8 (old)
  Each entry: 20-byte header + N regions of 20 bytes each
"""

import struct
from dataclasses import dataclass, field
from typing import List, Optional


@dataclass
class DARegion:
    m_buf: int = 0         # Offset in file where binary data is
    m_len: int = 0         # Size of binary data
    m_start_addr: int = 0  # Load address in device RAM
    m_start_offset: int = 0
    m_sig_len: int = 0     # Signature length at end of data


@dataclass
class DAEntry:
    magic: int = 0
    hw_code: int = 0
    hw_sub_code: int = 0
    hw_version: int = 0
    sw_version: int = 0
    pagesize: int = 0
    entry_region_index: int = 0
    entry_region_count: int = 0
    regions: List[DARegion] = field(default_factory=list)


def parse_da_blob(filepath: str) -> List[DAEntry]:
    """Parse a DA blob and return all entries."""
    entries = []

    with open(filepath, "rb") as f:
        data = f.read()

    if len(data) < 0x70:
        raise ValueError("DA file too small")

    # Read entry count at offset 0x68
    count_da = struct.unpack_from("<I", data, 0x68)[0]
    if count_da == 0 or count_da > 500:
        raise ValueError(f"Invalid DA entry count: {count_da}")

    # Detect old vs new format
    # New format: 0xDC per entry, Old format: 0xD8
    entry_size = 0xDC
    check_offset = 0x6C + 0xD8
    if check_offset + 2 <= len(data):
        marker = struct.unpack_from("<H", data, check_offset)[0]
        if marker == 0xDADA:
            entry_size = 0xD8

    for i in range(count_da):
        offset = 0x6C + (i * entry_size)
        if offset + 0x14 > len(data):
            break

        # Read 20-byte header
        hdr = data[offset:offset + 0x14]
        e = DAEntry()
        (e.magic, e.hw_code, e.hw_sub_code, e.hw_version,
         e.sw_version, _, e.pagesize, _,
         e.entry_region_index, e.entry_region_count) = struct.unpack_from(
            "<HHHHHHHHHH", hdr)

        # Read regions
        reg_offset = offset + 0x14
        for r in range(e.entry_region_count):
            ro = reg_offset + (r * 20)
            if ro + 20 > len(data):
                break
            region = DARegion()
            (region.m_buf, region.m_len, region.m_start_addr,
             region.m_start_offset, region.m_sig_len) = struct.unpack_from(
                "<IIIII", data, ro)
            e.regions.append(region)

        entries.append(e)

    return entries


def find_da_for_chip(filepath: str, hw_code: int,
                     hw_ver: int = 0xFFFF, sw_ver: int = 0xFFFF) -> Optional[DAEntry]:
    """Find DA entry matching a specific hw_code."""
    entries = parse_da_blob(filepath)

    for e in entries:
        if e.hw_code == hw_code:
            return e

    # Fallback: try matching without exact version
    for e in entries:
        if e.hw_code == hw_code:
            return e

    return None


def load_da_regions(filepath: str, entry: DAEntry) -> List[tuple]:
    """
    Load binary data for each region of a DA entry.
    Returns: [(load_addr, data_bytes, sig_len), ...]
    """
    regions = []
    with open(filepath, "rb") as f:
        file_data = f.read()

    for r in entry.regions:
        if r.m_len == 0 or r.m_buf == 0:
            continue
        if r.m_buf + r.m_len > len(file_data):
            print(f"[DA] Warning: region at {r.m_buf:#x} exceeds file size")
            continue
        region_data = file_data[r.m_buf:r.m_buf + r.m_len]
        regions.append((r.m_start_addr, region_data, r.m_sig_len))

    return regions


def da_info(filepath: str):
    """Print DA blob info."""
    entries = parse_da_blob(filepath)
    print(f"DA file: {filepath}")
    print(f"Entries: {len(entries)}")
    for e in entries:
        print(f"  hw={e.hw_code:#06x} sub={e.hw_sub_code:#06x} "
              f"ver={e.hw_version:#06x}/{e.sw_version:#06x} "
              f"regions={e.entry_region_count}")
        for i, r in enumerate(e.regions):
            print(f"    [{i}] addr={r.m_start_addr:#010x} "
                  f"len={r.m_len:#x} file_off={r.m_buf:#x} sig={r.m_sig_len:#x}")


if __name__ == "__main__":
    import sys
    if len(sys.argv) < 2:
        print("Usage: da_parser.py <DA_file.bin> [hw_code]")
        sys.exit(1)

    da_info(sys.argv[1])

    if len(sys.argv) >= 3:
        hw = int(sys.argv[2], 0)
        entry = find_da_for_chip(sys.argv[1], hw)
        if entry:
            print(f"\nMatch for {hw:#06x}:")
            print(f"  regions: {entry.entry_region_count}")
            for i, r in enumerate(entry.regions):
                print(f"  [{i}] load={r.m_start_addr:#010x} "
                      f"size={r.m_len} sig={r.m_sig_len}")
        else:
            print(f"\nNo match for {hw:#06x}")
