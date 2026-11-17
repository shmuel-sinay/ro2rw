"""
High-level flash orchestrator.
Coordinates scatter file, BROM, and DA to flash complete firmware.
Matches SP Flash Tool's FirmwareUpgradeCommand flow.
"""

import os
import sys
import time
from typing import Optional, Callable, List
from scatter_parser import ScatterFile, PartitionEntry, EmmcRegion, parse_scatter
from usb_connection import USBConnection, USBError
from brom import BromProtocol, BromError, DeviceInfo
from da_protocol import (
    DAInterface, DAError,
    HW_STORAGE_EMMC, HW_STORAGE_UFS,
    EMMC_BOOT1, EMMC_BOOT2, EMMC_USER,
)


class FlashError(Exception):
    pass


def _region_to_part_id(region: EmmcRegion) -> int:
    """Map scatter file region to DA partition ID."""
    mapping = {
        EmmcRegion.EMMC_BOOT1: EMMC_BOOT1,
        EmmcRegion.EMMC_BOOT2: EMMC_BOOT2,
        EmmcRegion.EMMC_USER: EMMC_USER,
    }
    return mapping.get(region, EMMC_USER)


def _format_size(size: int) -> str:
    if size >= 1024 * 1024 * 1024:
        return f"{size / (1024**3):.2f} GB"
    elif size >= 1024 * 1024:
        return f"{size / (1024**2):.1f} MB"
    elif size >= 1024:
        return f"{size / 1024:.1f} KB"
    return f"{size} B"


def _format_speed(bytes_per_sec: float) -> str:
    if bytes_per_sec >= 1024 * 1024:
        return f"{bytes_per_sec / (1024**2):.1f} MB/s"
    elif bytes_per_sec >= 1024:
        return f"{bytes_per_sec / 1024:.1f} KB/s"
    return f"{bytes_per_sec:.0f} B/s"


class ProgressBar:
    """Terminal progress bar with speed display."""

    def __init__(self, name: str, total: int):
        self.name = name
        self.total = total
        self.start_time = time.time()
        self.last_print = 0

    def update(self, current: int, total: int = None):
        if total:
            self.total = total

        now = time.time()
        if now - self.last_print < 0.1 and current < self.total:
            return  # Rate limit output
        self.last_print = now

        elapsed = now - self.start_time
        pct = (current * 100) // self.total if self.total > 0 else 0
        speed = current / elapsed if elapsed > 0 else 0

        bar_len = 30
        filled = (pct * bar_len) // 100
        bar = "█" * filled + "░" * (bar_len - filled)

        eta = ""
        if speed > 0 and current < self.total:
            remaining = (self.total - current) / speed
            eta = f" ETA: {remaining:.0f}s"

        print(
            f"\r  {self.name:<16} [{bar}] {pct:>3}% "
            f"({_format_size(current)}/{_format_size(self.total)}) "
            f"{_format_speed(speed)}{eta}   ",
            end="", flush=True
        )

        if current >= self.total:
            elapsed_str = f"{elapsed:.1f}s"
            print(f"\r  {self.name:<16} [{bar}] 100% "
                  f"({_format_size(self.total)}) "
                  f"{_format_speed(speed)} [{elapsed_str}]   ")


class MTKFlasher:
    """
    Main flash orchestrator - coordinates the complete firmware flash process.

    Flow (matching SP Flash Tool's FirmwareUpgradeCommand):
    1. Connect to BROM
    2. Get device info
    3. Upload DA
    4. Initialize DA communication
    5. For each partition in scatter file:
       a. Format partition (erase)
       b. Write ROM image data
       c. Verify checksum
    6. Finish and reboot
    """

    def __init__(self):
        self.usb = USBConnection()
        self.brom: Optional[BromProtocol] = None
        self.da: Optional[DAInterface] = None
        self.device_info: Optional[DeviceInfo] = None

    def connect(self, timeout_sec: float = 120) -> DeviceInfo:
        """Phase 1: Find device and establish BROM connection."""
        print("=" * 60)
        print("  MTK Flash Tool for macOS")
        print("=" * 60)
        print()

        if not self.usb.find_and_connect(timeout_sec):
            raise FlashError("No MTK device found. Make sure device is in BROM mode.")

        # Set high baud rate for USB CDC
        self.usb.set_line_coding(921600)

        self.brom = BromProtocol(self.usb)
        self.device_info = self.brom.init_device()

        print()
        return self.device_info

    def upload_da(self, da_path: str, da_addr: int = 0x200000):
        """Phase 2: Upload Download Agent to device."""
        if not self.brom:
            raise FlashError("Not connected to BROM")

        if not os.path.isfile(da_path):
            raise FlashError(f"DA file not found: {da_path}")

        print(f"\n[*] Uploading DA: {os.path.basename(da_path)}")
        self.brom.upload_and_start_da(da_path, da_addr)

        # Initialize DA communication
        self.da = DAInterface(self.usb)
        report = self.da.init()
        print(f"[*] DA ready: {report}")
        print()

    def flash_scatter(self, scatter_path: str,
                      partitions: List[str] = None,
                      format_all: bool = False,
                      skip_format: bool = False):
        """
        Phase 3: Flash firmware from scatter file.

        Args:
            scatter_path: Path to scatter file
            partitions: Optional list of partition names to flash (None = all enabled)
            format_all: Format entire storage before flashing
            skip_format: Skip format step (just write)
        """
        if not self.da:
            raise FlashError("DA not initialized")

        # Parse scatter file
        scatter = parse_scatter(scatter_path)
        print(f"[*] Scatter: {scatter.header.platform} ({scatter.header.storage})")
        print(f"[*] Partitions in scatter: {len(scatter.partitions)}")

        # Filter partitions
        if partitions:
            targets = []
            for name in partitions:
                p = scatter.get_partition_by_name(name)
                if p and p.file_path:
                    targets.append(p)
                else:
                    print(f"  Warning: partition '{name}' not found or no ROM file")
        else:
            targets = scatter.get_enabled_partitions()

        if not targets:
            raise FlashError("No partitions to flash (no ROM files found?)")

        # Calculate total size
        total_size = sum(os.path.getsize(p.file_path) for p in targets)
        print(f"\n[*] Flashing {len(targets)} partitions ({_format_size(total_size)}):")
        for p in targets:
            fsize = os.path.getsize(p.file_path)
            print(f"    {p.name:<20} @ {p.start_addr:#012x}  "
                  f"({_format_size(fsize)})")

        print()

        # Determine storage type
        storage_type = HW_STORAGE_EMMC
        if scatter.header.storage.upper() == "UFS":
            storage_type = HW_STORAGE_UFS

        # Flash each partition
        flash_start = time.time()
        success_count = 0

        for i, part in enumerate(targets):
            print(f"[{i+1}/{len(targets)}] {part.name}")

            # Read ROM file
            with open(part.file_path, "rb") as f:
                rom_data = f.read()

            part_id = _region_to_part_id(part.region)

            # Format partition (unless skipped)
            if not skip_format:
                fmt_size = max(len(rom_data), part.size) if part.size > 0 else len(rom_data)
                try:
                    progress = ProgressBar(f"  Format", fmt_size)
                    self.da.format_partition(
                        part.start_addr, fmt_size,
                        part_id=part_id,
                        storage_type=storage_type,
                        progress_cb=progress.update
                    )
                except DAError as e:
                    print(f"  Format warning: {e} (continuing...)")

            # Write ROM data
            progress = ProgressBar(f"  Write", len(rom_data))
            try:
                self.da.write_partition(
                    part.start_addr, rom_data,
                    part_id=part_id,
                    storage_type=storage_type,
                    progress_cb=progress.update
                )
                success_count += 1
            except DAError as e:
                print(f"\n  ERROR writing {part.name}: {e}")
                raise FlashError(f"Failed to write partition {part.name}: {e}")

        # Summary
        total_time = time.time() - flash_start
        avg_speed = total_size / total_time if total_time > 0 else 0

        print()
        print("=" * 60)
        print(f"  Flash complete: {success_count}/{len(targets)} partitions")
        print(f"  Total: {_format_size(total_size)} in {total_time:.1f}s "
              f"({_format_speed(avg_speed)})")
        print("=" * 60)

    def finish(self, reboot: bool = True):
        """Phase 4: Finalize and optionally reboot."""
        if self.da:
            action = "Rebooting" if reboot else "Powering off"
            print(f"\n[*] {action} device...")
            self.da.shutdown(reboot=reboot)

        if self.usb:
            self.usb.disconnect()

        print("[*] Done!")

    def flash_firmware(self, scatter_path: str, da_path: str,
                       da_addr: int = 0x200000,
                       partitions: List[str] = None,
                       skip_format: bool = False,
                       reboot: bool = True):
        """
        Complete firmware flash - all phases in one call.

        Args:
            scatter_path: Path to scatter file
            da_path: Path to DA binary
            da_addr: DA load address in RAM
            partitions: Optional list of specific partitions to flash
            skip_format: Skip format/erase step
            reboot: Reboot after flash
        """
        try:
            self.connect()
            self.upload_da(da_path, da_addr)
            self.flash_scatter(scatter_path, partitions, skip_format=skip_format)
            self.finish(reboot=reboot)
        except (FlashError, BromError, DAError, USBError) as e:
            print(f"\n[ERROR] {e}")
            self.finish(reboot=False)
            raise
        except KeyboardInterrupt:
            print("\n\n[!] Interrupted by user")
            self.finish(reboot=False)
            sys.exit(1)
