"""
MTK Scatter File Parser
Supports both v1 (text) and v2 (text with headers) scatter file formats.
Extracts partition info: name, start address, size, file path, region, storage type.
"""

import re
import os
from dataclasses import dataclass, field
from enum import IntEnum
from typing import List, Optional


class StorageType(IntEnum):
    EMMC = 0x1
    SDMMC = 0x2
    NAND = 0x10
    NOR = 0x20
    UFS = 0x30


class EmmcRegion(IntEnum):
    EMMC_BOOT1 = 0
    EMMC_BOOT2 = 1
    EMMC_RPMB = 2
    EMMC_GP1 = 3
    EMMC_GP2 = 4
    EMMC_GP3 = 5
    EMMC_GP4 = 6
    EMMC_USER = 7


class OperationType(IntEnum):
    BOOTLOADERS = 0
    UPDATE = 1
    INVISIBLE = 2
    PROTECTED = 3
    BINREGION = 4
    RESERVED = 5


class RomType(IntEnum):
    NONE = 0x00
    NORMAL = 0x01
    SV5_BL = 0x07
    YAFFS = 0x08
    UBI = 0x10
    EXT4 = 0x11
    FAT = 0x12
    F2FS = 0x13


@dataclass
class ScatterHeader:
    version: str = ""
    platform: str = ""
    project: str = ""
    storage: str = "EMMC"
    block_size: int = 0x20000


@dataclass
class PartitionEntry:
    name: str = ""
    file_name: str = ""
    start_addr: int = 0
    size: int = 0
    storage: StorageType = StorageType.EMMC
    region: EmmcRegion = EmmcRegion.EMMC_USER
    operation: OperationType = OperationType.UPDATE
    is_download: bool = True
    enabled: bool = True
    is_reserved: bool = False
    rom_type: RomType = RomType.NORMAL
    file_path: str = ""  # resolved absolute path to ROM file


@dataclass
class ScatterFile:
    header: ScatterHeader = field(default_factory=ScatterHeader)
    partitions: List[PartitionEntry] = field(default_factory=list)
    scatter_dir: str = ""  # directory containing scatter file

    def get_enabled_partitions(self) -> List[PartitionEntry]:
        return [p for p in self.partitions if p.enabled and p.is_download]

    def get_partition_by_name(self, name: str) -> Optional[PartitionEntry]:
        for p in self.partitions:
            if p.name.lower() == name.lower():
                return p
        return None

    def summary(self) -> str:
        lines = [
            f"Platform: {self.header.platform}",
            f"Storage:  {self.header.storage}",
            f"Version:  {self.header.version}",
            f"Partitions: {len(self.partitions)}",
            "",
            f"{'Name':<20} {'Start':>14} {'Size':>14} {'File':<30} {'DL'}",
            "-" * 85,
        ]
        for p in self.partitions:
            fname = os.path.basename(p.file_name) if p.file_name else "-"
            dl = "Y" if (p.enabled and p.is_download) else "N"
            lines.append(
                f"{p.name:<20} {p.start_addr:>#14x} {p.size:>#14x} {fname:<30} {dl}"
            )
        return "\n".join(lines)


def _parse_storage_type(s: str) -> StorageType:
    s = s.strip().upper()
    # Handle both "EMMC" and "HW_STORAGE_EMMC" formats
    s = s.replace("HW_STORAGE_", "")
    mapping = {
        "EMMC": StorageType.EMMC,
        "NAND": StorageType.NAND,
        "NOR": StorageType.NOR,
        "UFS": StorageType.UFS,
        "SDMMC": StorageType.SDMMC,
    }
    return mapping.get(s, StorageType.EMMC)


def _parse_region(s: str) -> EmmcRegion:
    s = s.strip().upper()
    mapping = {
        "EMMC_BOOT_1": EmmcRegion.EMMC_BOOT1,
        "EMMC_BOOT1": EmmcRegion.EMMC_BOOT1,
        "EMMC_BOOT_2": EmmcRegion.EMMC_BOOT2,
        "EMMC_BOOT2": EmmcRegion.EMMC_BOOT2,
        "EMMC_BOOT1_BOOT2": EmmcRegion.EMMC_BOOT1,
        "EMMC_BOOT_1_BOOT_2": EmmcRegion.EMMC_BOOT1,
        "EMMC_RPMB": EmmcRegion.EMMC_RPMB,
        "EMMC_USER": EmmcRegion.EMMC_USER,
    }
    return mapping.get(s, EmmcRegion.EMMC_USER)


def _parse_operation(s: str) -> OperationType:
    s = s.strip().upper()
    mapping = {
        "BOOTLOADERS": OperationType.BOOTLOADERS,
        "UPDATE": OperationType.UPDATE,
        "INVISIBLE": OperationType.INVISIBLE,
        "PROTECTED": OperationType.PROTECTED,
        "BINREGION": OperationType.BINREGION,
        "RESERVED": OperationType.RESERVED,
    }
    return mapping.get(s, OperationType.UPDATE)


def _parse_bool(s: str) -> bool:
    return s.strip().lower() in ("true", "yes", "1")


def _parse_int(s: str) -> int:
    s = s.strip()
    if s.startswith("0x") or s.startswith("0X"):
        return int(s, 16)
    return int(s) if s else 0


def _parse_rom_type(s: str) -> RomType:
    s = s.strip().upper()
    mapping = {
        "NORMAL_ROM": RomType.NORMAL,
        "YAFFS_IMG": RomType.YAFFS,
        "UBI_IMG": RomType.UBI,
        "EXT4_IMG": RomType.EXT4,
        "FAT_IMG": RomType.FAT,
        "F2FS_IMG": RomType.F2FS,
    }
    return mapping.get(s, RomType.NORMAL)


def parse_scatter_v2(content: str, scatter_dir: str) -> ScatterFile:
    """Parse v2 scatter file (modern format with section headers)."""
    sf = ScatterFile(scatter_dir=scatter_dir)

    # Parse general header (indented YAML-like keys)
    for key, val in re.findall(r"^\s*(\w+):\s*(.+)$", content, re.MULTILINE):
        key_l = key.lower()
        if key_l == "platform":
            sf.header.platform = val.strip()
        elif key_l == "project":
            sf.header.project = val.strip()
        elif key_l == "storage":
            sf.header.storage = val.strip().replace("HW_STORAGE_", "")
        elif key_l == "config_version":
            sf.header.version = val.strip()
        elif key_l == "block_size":
            sf.header.block_size = _parse_int(val)

    # Split into partition sections (each starts with "- partition_index:")
    sections = re.split(r"^-\s*partition_index:", content, flags=re.MULTILINE)

    for section in sections[1:]:  # skip preamble
        entry = PartitionEntry()
        kv = {}
        for m in re.finditer(r"^\s*(\w+):\s*(.+)$", section, re.MULTILINE):
            kv[m.group(1).lower()] = m.group(2).strip()

        entry.name = kv.get("partition_name", "")
        entry.file_name = kv.get("file_name", "")
        entry.start_addr = _parse_int(kv.get("physical_start_addr", "0"))
        entry.size = _parse_int(kv.get("partition_size", "0"))
        entry.storage = _parse_storage_type(kv.get("storage", "EMMC"))
        entry.region = _parse_region(kv.get("region", "EMMC_USER"))
        entry.operation = _parse_operation(kv.get("operation_type", "UPDATE"))
        entry.is_download = _parse_bool(kv.get("is_download", "true"))
        entry.is_reserved = _parse_bool(kv.get("is_reserved", "false"))
        entry.rom_type = _parse_rom_type(kv.get("type", "NORMAL_ROM"))

        if kv.get("d_type", "").upper() == "NONE" and not entry.file_name:
            entry.is_download = False

        # Resolve file path
        if entry.file_name and entry.file_name.lower() != "none":
            candidate = os.path.join(scatter_dir, entry.file_name)
            if os.path.isfile(candidate):
                entry.file_path = candidate
                entry.enabled = True
            else:
                entry.enabled = False
        else:
            entry.enabled = False
            entry.is_download = False

        sf.partitions.append(entry)

    return sf


def parse_scatter_v1(content: str, scatter_dir: str) -> ScatterFile:
    """Parse v1 scatter file (legacy format: partition_name addr)."""
    sf = ScatterFile(scatter_dir=scatter_dir)
    sf.header.version = "v1"

    lines = content.strip().splitlines()
    partitions = []

    for line in lines:
        line = line.strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue

        # v1 format: partition_name start_address [size] [file_name]
        parts = line.split()
        if len(parts) >= 2:
            entry = PartitionEntry()
            entry.name = parts[0]
            entry.start_addr = _parse_int(parts[1])
            if len(parts) >= 3:
                entry.size = _parse_int(parts[2])
            if len(parts) >= 4:
                entry.file_name = parts[3]
                candidate = os.path.join(scatter_dir, entry.file_name)
                if os.path.isfile(candidate):
                    entry.file_path = candidate
                    entry.enabled = True
            partitions.append(entry)

    # Calculate sizes from addresses if not provided
    for i, p in enumerate(partitions):
        if p.size == 0 and i + 1 < len(partitions):
            p.size = partitions[i + 1].start_addr - p.start_addr

    sf.partitions = partitions
    return sf


def parse_scatter(filepath: str) -> ScatterFile:
    """Auto-detect scatter file version and parse."""
    scatter_dir = os.path.dirname(os.path.abspath(filepath))

    with open(filepath, "r", encoding="utf-8", errors="ignore") as f:
        content = f.read()

    # Detect version: v2 has "partition_index:" markers
    if "partition_index:" in content:
        return parse_scatter_v2(content, scatter_dir)
    else:
        return parse_scatter_v1(content, scatter_dir)


def _normalize_stem(name: str) -> str:
    """Reduce a filename/partition name to a comparable stem: drop directory,
    extension, common vendor tags (-verified/-signed/...), and separators.
    e.g. 'lk-verified.img' -> 'lk', 'boot.img' -> 'boot', 'vbmeta_a' -> 'vbmetaa'."""
    n = os.path.basename(str(name)).lower()
    n = os.path.splitext(n)[0]
    for tag in ("-verified", "_verified", ".verified", "-signed", "_signed",
                "-sign", "_sign", "-unsigned", "_unsigned", "-cmpr", "_cmpr"):
        n = n.replace(tag, "")
    return re.sub(r"[^a-z0-9]+", "", n)


def auto_load_roms(scatter: ScatterFile, rom_dir: str = None) -> int:
    """Resolve ROM files for partitions, tolerant of filename variations.

    The scatter may say 'lk.img' while the folder has 'lk-verified.img' (this
    exact mismatch silently skipped critical partitions before). We index the
    actual files by a normalized stem and match the scatter's file_name — and,
    as a fallback, the partition name (handling A/B '_a'/'_b' slot suffixes).
    Returns the count of newly resolved files."""
    if rom_dir is None:
        rom_dir = scatter.scatter_dir
    if not rom_dir or not os.path.isdir(rom_dir):
        return 0

    # index real files by normalized stem (first match wins)
    index = {}
    for fn in os.listdir(rom_dir):
        full = os.path.join(rom_dir, fn)
        if os.path.isfile(full):
            index.setdefault(_normalize_stem(fn), full)

    count = 0
    for p in scatter.partitions:
        if p.file_path and os.path.isfile(p.file_path):
            continue
        if not p.file_name or p.file_name.lower() == "none":
            continue

        hit = None
        # 1. exact filename
        cand = os.path.join(rom_dir, p.file_name)
        if os.path.isfile(cand):
            hit = cand
        # 2. normalized match on the scatter's file_name (handles -verified etc.)
        if not hit:
            hit = index.get(_normalize_stem(p.file_name))
        # 3. fall back to the partition name, with A/B slot suffix stripped
        if not hit and p.name:
            pname = p.name
            if pname.lower().endswith(("_a", "_b")):
                pname = pname[:-2]
            hit = index.get(_normalize_stem(pname))

        if hit:
            p.file_path = hit
            p.enabled = True
            count += 1

    return count
