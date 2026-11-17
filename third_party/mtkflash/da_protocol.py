"""
MTK Download Agent (DA) Protocol Implementation.
Handles communication with the DA running on-device for flash operations.

Supports two protocol variants:
- Legacy DA: older MTK chips, direct binary commands
- XFlash DA: modern MTK chips, structured packet protocol

Based on SP Flash Tool's libflashtool methodology:
- Large bulk transfers (1MB DRAM packets) for speed
- DA-side erase+write for efficiency
- Checksum verification at USB and storage levels
"""

import struct
import time
import os
from enum import IntEnum
from typing import Optional, Callable, Tuple, List
from dataclasses import dataclass
from usb_connection import USBConnection, USBError


# DA Commands (from SP Flash Tool DOWNLOAD.H)
class DACmd:
    # Storage write commands
    SDMMC_SWITCH_PART = 0x60
    SDMMC_WRITE_IMAGE = 0x61
    SDMMC_WRITE_DATA = 0x62
    SDMMC_GET_CARD_TYPE = 0x63

    UFS_SWITCH_PART = 0x80
    UFS_WRITE_IMAGE = 0x81
    UFS_WRITE_DATA = 0x82
    UFS_READ_GPT = 0x85
    UFS_WRITE_GPT = 0x89

    NAND_WRITE_DATA = 0xB3

    # General commands
    POST_PROCESS = 0xD1
    SPEED_CMD = 0xD2
    MEM_CMD = 0xD3
    FORMAT_CMD = 0xD4
    WRITE_CMD = 0xD5
    READ_CMD = 0xD6
    FINISH_CMD = 0xD9
    ENABLE_WATCHDOG = 0xDB
    ENABLE_DRAM = 0xE8

    WRITE_PARTITION_TBL = 0xA1
    READ_PARTITION_TBL = 0xA2
    SDMMC_WRITE_PMT = 0xA4
    SDMMC_READ_PMT = 0xA5
    SDMMC_WRITE_GPT = 0xA9

    # Security
    CHK_PC_SEC_INFO = 0xE0
    SECURE_USB_WRITE = 0xBA
    VERIFY_IMG_CHKSUM = 0xBD

    # USB
    USB_SETUP_PORT = 0x70
    USB_CHECK_STATUS = 0x72

    # Battery/power
    GET_BATTERY_VOLTAGE = 0xD0

    # Project info
    GET_PROJECT_ID = 0xEF


# Response codes
SOC_OK = 0xC1
SOC_FAIL = 0xCF
SYNC_CHAR = 0xC0
CONT_CHAR = 0x69
STOP_CHAR = 0x96
ACK = 0x5A
NACK = 0xA5

# Storage types (from DOWNLOAD.H)
HW_STORAGE_EMMC = 2
HW_STORAGE_UFS = 4

# eMMC partition IDs
EMMC_BOOT1 = 1
EMMC_BOOT2 = 2
EMMC_RPMB = 3
EMMC_USER = 8

# Transfer sizes - matching SP Flash Tool for speed
DRAM_PACKET_SIZE = 0x100000  # 1MB - SP Flash Tool's DRAM packet size
SRAM_PACKET_SIZE = 0x2000    # 8KB - for SRAM phase


class DAError(Exception):
    pass


@dataclass
class DAReport:
    """DA device report after initialization."""
    da_major_ver: int = 0
    da_minor_ver: int = 0
    chip_type: int = 0
    emmc_size: int = 0
    ufs_size: int = 0
    nand_size: int = 0
    dram_size: int = 0
    dram_type: int = 0
    storage_type: int = 0  # HW_STORAGE_EMMC or HW_STORAGE_UFS

    def __str__(self):
        storage = "eMMC" if self.storage_type == HW_STORAGE_EMMC else "UFS"
        size_gb = max(self.emmc_size, self.ufs_size) / (1024 ** 3)
        return (
            f"DA v{self.da_major_ver}.{self.da_minor_ver} | "
            f"Storage: {storage} ({size_gb:.1f} GB) | "
            f"DRAM: {self.dram_size // (1024*1024)} MB"
        )


# ============================================================================
# XFlash DA Protocol (modern MTK devices)
# ============================================================================

class XFlashCmd:
    """XFlash DA command IDs (sent inside structured packets)."""
    # Connection
    SYNC = 0
    DEVICE_CTRL = 1
    CC_OPTIONAL_DOWNLOAD_ACT = 2

    # Storage ops
    WRITE_DATA = 3
    READ_DATA = 4
    FORMAT_DATA = 5
    CHECKSUM_DATA = 6

    # Partition
    READ_PARTITION_TABLE = 7
    WRITE_PARTITION_TABLE = 8
    READ_EFUSE = 9
    WRITE_EFUSE = 10

    # Shutdown
    SHUTDOWN = 11

    # Misc
    SET_RUNTIME_PARAMETER = 12
    SET_HOST_INFO = 13
    SET_META_BOOT_MODE = 14

    # Extended
    GET_PACKET_LENGTH = 15
    SET_CHECKSUM_LEVEL = 16

    EMMC_CONTROL = 17
    LIFE_CYCLE_STATUS = 19

    # DRAM
    DRAM_REPAIR = 20

    # Debug
    GET_DA_INFO = 0x80
    GET_BROM_INFO = 0x81


class XFlashMagic:
    """XFlash protocol magic numbers."""
    DATA = 0xFEEEEEEF
    STATUS = 0xD0D0D0D0


class XFlashDataType:
    PROTOCOL_FLOW = 1
    MESSAGE = 2


class XFlashProtocol:
    """
    XFlash DA protocol - used by modern MTK devices.
    Structured packet format with large transfer support.
    """

    def __init__(self, usb: USBConnection):
        self.usb = usb
        self.write_packet_size = DRAM_PACKET_SIZE
        self.read_packet_size = DRAM_PACKET_SIZE

    def send_param(self, params: list):
        """Send parameter list as structured packet."""
        data = b""
        for p in params:
            if isinstance(p, int):
                data += struct.pack("<I", p)
            elif isinstance(p, bytes):
                data += p
            else:
                data += bytes(p)

        # Pad to 512-byte boundary for USB efficiency
        if len(data) % 512 != 0:
            data += b'\x00' * (512 - (len(data) % 512))

        self.usb.write(data)

    def recv_param(self, count: int = 1) -> list:
        """Receive parameter words."""
        data = self.usb.read_exact(count * 4)
        return list(struct.unpack(f"<{count}I", data))

    def send_command(self, cmd: int, sub_cmd: int = 0) -> int:
        """
        Send XFlash command and get status.
        Packet: [cmd(4)] [sub_cmd(4)]
        """
        self.send_param([cmd, sub_cmd])
        status = self.recv_param(1)[0]
        if status != 0:
            raise DAError(f"XFlash cmd {cmd:#x} sub {sub_cmd:#x} "
                          f"failed: status={status:#x}")
        return status

    def get_packet_length(self) -> Tuple[int, int]:
        """Query optimal packet sizes from DA."""
        self.send_param([XFlashCmd.GET_PACKET_LENGTH, 0])
        result = self.recv_param(3)  # status, write_size, read_size
        if result[0] != 0:
            print(f"[DA] get_packet_length status={result[0]:#x}, using defaults")
            return DRAM_PACKET_SIZE, DRAM_PACKET_SIZE

        self.write_packet_size = result[1]
        self.read_packet_size = result[2]
        print(f"[DA] Packet sizes: write={self.write_packet_size:#x}, "
              f"read={self.read_packet_size:#x}")
        return self.write_packet_size, self.read_packet_size

    def set_checksum_level(self, level: int = 0):
        """Set checksum verification level.
        0 = none, 1 = USB, 2 = storage, 3 = all
        """
        self.send_param([XFlashCmd.SET_CHECKSUM_LEVEL, 0])
        self.send_param([level])
        status = self.recv_param(1)[0]
        if status != 0:
            print(f"[DA] Warning: set_checksum_level status={status:#x}")

    def set_host_info(self, host_os: int = 10):
        """Tell DA what host OS we are (10=Mac, 11=Windows, 12=Linux)."""
        try:
            self.send_param([XFlashCmd.SET_HOST_INFO, 0])
            self.send_param([host_os])
            self.recv_param(1)
        except (DAError, USBError):
            pass  # Not critical

    def write_data(self, addr: int, data: bytes, storage_type: int = HW_STORAGE_EMMC,
                   part_id: int = EMMC_USER, progress_cb: Callable = None) -> bool:
        """
        Write data to flash storage via DA.
        Uses large bulk transfers for maximum speed.

        Args:
            addr: Flash address to write to
            data: Binary data to write
            storage_type: HW_STORAGE_EMMC or HW_STORAGE_UFS
            part_id: Partition ID (EMMC_BOOT1, EMMC_USER, etc.)
            progress_cb: Callback(sent_bytes, total_bytes)
        """
        total = len(data)
        pkt_size = self.write_packet_size

        # Send write command
        self.send_param([XFlashCmd.WRITE_DATA, 0])
        # Send write parameters: addr, length, storage_type, part_id
        self.send_param([
            addr & 0xFFFFFFFF,          # addr low
            (addr >> 32) & 0xFFFFFFFF,  # addr high
            total & 0xFFFFFFFF,         # length low
            (total >> 32) & 0xFFFFFFFF, # length high
            storage_type,
            part_id,
        ])

        # Check if DA accepted
        status = self.recv_param(1)[0]
        if status != 0:
            raise DAError(f"Write command rejected: status={status:#x}")

        # Send data in large chunks
        sent = 0
        while sent < total:
            chunk_len = min(pkt_size, total - sent)
            chunk = data[sent:sent + chunk_len]

            # Calculate chunk checksum
            checksum = sum(chunk) & 0xFFFF

            # Send: flags(4) + checksum(4) + data
            # Pad data to 512-byte boundary
            padded = chunk
            pad_len = (512 - (len(padded) % 512)) % 512
            if pad_len:
                padded = padded + b'\x00' * pad_len

            header = struct.pack("<II", 0, checksum)
            self.usb.write(header + padded, timeout_ms=30000)

            sent += chunk_len

            if progress_cb:
                progress_cb(sent, total)

        # Wait for final status
        status = self.recv_param(1)[0]
        if status != 0:
            raise DAError(f"Write data failed: status={status:#x}")

        return True

    def format_data(self, addr: int, length: int,
                    storage_type: int = HW_STORAGE_EMMC,
                    part_id: int = EMMC_USER,
                    progress_cb: Callable = None) -> bool:
        """Format (erase) a flash region."""
        self.send_param([XFlashCmd.FORMAT_DATA, 0])
        self.send_param([
            addr & 0xFFFFFFFF,
            (addr >> 32) & 0xFFFFFFFF,
            length & 0xFFFFFFFF,
            (length >> 32) & 0xFFFFFFFF,
            storage_type,
            part_id,
        ])

        status = self.recv_param(1)[0]
        if status != 0:
            raise DAError(f"Format failed: status={status:#x}")

        # Wait for format complete (DA sends progress updates)
        while True:
            try:
                result = self.recv_param(1)[0]
                if result == 0:
                    break
                if progress_cb:
                    progress_cb(result, 100)
            except USBError:
                break

        return True

    def read_data(self, addr: int, length: int,
                  storage_type: int = HW_STORAGE_EMMC,
                  part_id: int = EMMC_USER,
                  progress_cb: Callable = None) -> bytes:
        """Read data from flash storage."""
        pkt_size = self.read_packet_size

        self.send_param([XFlashCmd.READ_DATA, 0])
        self.send_param([
            addr & 0xFFFFFFFF,
            (addr >> 32) & 0xFFFFFFFF,
            length & 0xFFFFFFFF,
            (length >> 32) & 0xFFFFFFFF,
            storage_type,
            part_id,
        ])

        status = self.recv_param(1)[0]
        if status != 0:
            raise DAError(f"Read command failed: status={status:#x}")

        result = bytearray()
        received = 0
        while received < length:
            chunk_len = min(pkt_size, length - received)
            # Read: checksum(4) + data
            resp = self.usb.read(chunk_len + 4, timeout_ms=30000)
            if len(resp) > 4:
                result.extend(resp[4:])
                received += len(resp) - 4
            if progress_cb:
                progress_cb(received, length)

            # Acknowledge chunk
            self.send_param([0])

        # Final status
        self.recv_param(1)
        return bytes(result[:length])

    def shutdown(self, reboot: bool = True):
        """Shutdown DA and optionally reboot device."""
        try:
            self.send_param([XFlashCmd.SHUTDOWN, 0])
            self.send_param([1 if reboot else 0])
            self.recv_param(1)
        except (DAError, USBError):
            pass


# ============================================================================
# Legacy DA Protocol (older MTK devices)
# ============================================================================

class LegacyDAProtocol:
    """
    Legacy DA protocol for older MTK chips.
    Direct byte-command based protocol.
    """

    def __init__(self, usb: USBConnection):
        self.usb = usb
        self.da_report: Optional[DAReport] = None

    def _send_cmd(self, cmd: int):
        """Send single-byte command."""
        self.usb.write(bytes([cmd]))

    def _recv_byte(self) -> int:
        resp = self.usb.read(1, timeout_ms=5000)
        return resp[0] if resp else 0

    def _send_dword(self, val: int):
        self.usb.write(struct.pack(">I", val))

    def _recv_dword(self) -> int:
        data = self.usb.read_exact(4)
        return struct.unpack(">I", data)[0]

    def _send_qword(self, val: int):
        self.usb.write(struct.pack(">Q", val))

    def _recv_qword(self) -> int:
        data = self.usb.read_exact(8)
        return struct.unpack(">Q", data)[0]

    def recv_da_report(self) -> DAReport:
        """Receive DA initialization report."""
        report = DAReport()

        # DA sends identification data after starting
        report.da_major_ver = self._recv_byte()
        report.da_minor_ver = self._recv_byte()

        # Read storage detection
        # This varies by DA version, basic structure:
        data = self.usb.read(64, timeout_ms=5000)

        # Parse what we can (format depends on DA version)
        if len(data) >= 16:
            report.emmc_size = struct.unpack(">Q", data[0:8])[0]
            report.dram_size = struct.unpack(">I", data[8:12])[0]
            report.storage_type = data[12]

        self.da_report = report
        print(f"[DA] {report}")
        return report

    def switch_emmc_part(self, part_id: int):
        """Switch to specific eMMC partition."""
        self._send_cmd(DACmd.SDMMC_SWITCH_PART)
        self._send_dword(part_id)
        resp = self._recv_byte()
        if resp != SOC_OK:
            raise DAError(f"Switch partition failed: {resp:#04x}")

    def write_emmc_image(self, addr: int, data: bytes,
                         part_id: int = EMMC_USER,
                         progress_cb: Callable = None) -> bool:
        """
        Write image to eMMC via Legacy DA protocol.
        Uses 1MB bulk transfers matching SP Flash Tool's DRAM_PACKET_SIZE.
        """
        total = len(data)
        packet_size = DRAM_PACKET_SIZE

        # Switch to target partition if needed
        if part_id != EMMC_USER:
            self.switch_emmc_part(part_id)

        # Send write command
        self._send_cmd(DACmd.SDMMC_WRITE_IMAGE)

        # Send parameters: address (8 bytes), length (8 bytes), packet_size (4 bytes)
        self._send_qword(addr)
        self._send_qword(total)
        self._send_dword(packet_size)

        # Wait for ACK
        resp = self._recv_byte()
        if resp != ACK:
            raise DAError(f"Write image not acknowledged: {resp:#04x}")

        # Send data in bulk chunks
        sent = 0
        while sent < total:
            chunk_len = min(packet_size, total - sent)
            chunk = data[sent:sent + chunk_len]

            # Pad last chunk to packet boundary
            if len(chunk) < packet_size and sent + chunk_len >= total:
                chunk = chunk + b'\x00' * (packet_size - len(chunk))

            # Calculate checksum before sending
            checksum = sum(chunk) & 0xFFFF

            # Bulk write the chunk
            self.usb.write(chunk, timeout_ms=30000)

            # Send checksum
            self.usb.write(struct.pack(">H", checksum))

            # Wait for chunk ACK
            resp = self._recv_byte()
            if resp != CONT_CHAR and resp != ACK:
                raise DAError(
                    f"Write chunk failed at offset {sent:#x}: {resp:#04x}"
                )

            sent += chunk_len

            if progress_cb:
                progress_cb(sent, total)

        # Wait for final status
        resp = self._recv_byte()
        if resp != SOC_OK:
            raise DAError(f"Write image final status: {resp:#04x}")

        return True

    def format_emmc(self, addr: int, length: int,
                    part_id: int = EMMC_USER,
                    progress_cb: Callable = None) -> bool:
        """Format (erase) eMMC region."""
        if part_id != EMMC_USER:
            self.switch_emmc_part(part_id)

        self._send_cmd(DACmd.FORMAT_CMD)
        # Format type: 0=normal, 1=force, 2=mark bad
        self._send_dword(0)  # normal format
        self._send_qword(addr)
        self._send_qword(length)

        resp = self._recv_byte()
        if resp != ACK:
            raise DAError(f"Format not acknowledged: {resp:#04x}")

        # Wait for format to complete
        while True:
            resp = self._recv_byte()
            if resp == SOC_OK:
                break
            if resp == SOC_FAIL:
                raise DAError("Format failed")
            # resp might be progress percentage
            if progress_cb and 0 < resp < 100:
                progress_cb(resp, 100)

        return True

    def read_emmc(self, addr: int, length: int,
                  part_id: int = EMMC_USER,
                  progress_cb: Callable = None) -> bytes:
        """Read data from eMMC."""
        if part_id != EMMC_USER:
            self.switch_emmc_part(part_id)

        packet_size = DRAM_PACKET_SIZE

        self._send_cmd(DACmd.READ_CMD)
        self._send_qword(addr)
        self._send_qword(length)
        self._send_dword(packet_size)

        resp = self._recv_byte()
        if resp != ACK:
            raise DAError(f"Read not acknowledged: {resp:#04x}")

        result = bytearray()
        received = 0
        while received < length:
            chunk_len = min(packet_size, length - received)
            chunk = self.usb.read(chunk_len, timeout_ms=30000)
            result.extend(chunk)
            received += len(chunk)

            # ACK chunk
            self.usb.write(bytes([ACK]))

            if progress_cb:
                progress_cb(received, length)

        return bytes(result[:length])

    def finish(self, reboot: bool = True):
        """Send finish command to DA."""
        self._send_cmd(DACmd.FINISH_CMD)
        if reboot:
            self._send_dword(0)  # 0 = reboot
        else:
            self._send_dword(1)  # 1 = power off


# ============================================================================
# Unified DA Interface
# ============================================================================

class DAInterface:
    """
    Unified Download Agent interface.
    Auto-detects protocol variant (XFlash vs Legacy) and provides
    common API for flash operations.
    """

    def __init__(self, usb: USBConnection):
        self.usb = usb
        self.xflash: Optional[XFlashProtocol] = None
        self.legacy: Optional[LegacyDAProtocol] = None
        self.is_xflash = False
        self.report: Optional[DAReport] = None

    def init(self) -> DAReport:
        """
        Initialize DA connection and detect protocol variant.
        Must be called after BROM uploads and jumps to DA.
        """
        print("[DA] Initializing connection...")

        # Read whatever the DA sends first (diagnostic)
        try:
            initial = self.usb.read(256, timeout_ms=2000)
            if initial:
                hex_str = " ".join(f"{b:02X}" for b in initial[:32])
                ascii_str = "".join(chr(b) if 32 <= b < 127 else "." for b in initial[:32])
                print(f"[DA] Initial data: [{hex_str}] '{ascii_str}'")
                if b"READY" in initial:
                    print("[DA] WARNING: Device is back in preloader (READY), DA did not start!")
                    raise DAError("DA failed to start - device rebooted to preloader")
        except DAError:
            raise
        except Exception:
            print("[DA] No initial data from DA")

        # Try XFlash protocol first (modern devices)
        try:
            self.xflash = XFlashProtocol(self.usb)

            # XFlash sync - send sync command
            self.xflash.send_param([XFlashCmd.SYNC, 0])
            result = self.xflash.recv_param(1)

            if result[0] == 0:
                self.is_xflash = True
                print("[DA] XFlash protocol detected")

                # Get optimal packet sizes
                self.xflash.get_packet_length()

                # Set host info (macOS = 10)
                self.xflash.set_host_info(10)

                self.report = DAReport()
                return self.report

        except (DAError, USBError, Exception) as e:
            print(f"[DA] XFlash detection failed ({e}), trying Legacy...")
            self.xflash = None

        # Fall back to Legacy protocol
        self.legacy = LegacyDAProtocol(self.usb)
        self.is_xflash = False
        print("[DA] Legacy protocol mode")

        try:
            self.report = self.legacy.recv_da_report()
        except Exception:
            self.report = DAReport()

        return self.report

    def write_partition(self, addr: int, data: bytes,
                        part_id: int = EMMC_USER,
                        storage_type: int = HW_STORAGE_EMMC,
                        progress_cb: Callable = None) -> bool:
        """Write data to flash partition."""
        if self.is_xflash and self.xflash:
            return self.xflash.write_data(
                addr, data, storage_type, part_id, progress_cb
            )
        elif self.legacy:
            return self.legacy.write_emmc_image(
                addr, data, part_id, progress_cb
            )
        else:
            raise DAError("No DA protocol initialized")

    def format_partition(self, addr: int, length: int,
                         part_id: int = EMMC_USER,
                         storage_type: int = HW_STORAGE_EMMC,
                         progress_cb: Callable = None) -> bool:
        """Format (erase) a flash region."""
        if self.is_xflash and self.xflash:
            return self.xflash.format_data(
                addr, length, storage_type, part_id, progress_cb
            )
        elif self.legacy:
            return self.legacy.format_emmc(
                addr, length, part_id, progress_cb
            )
        else:
            raise DAError("No DA protocol initialized")

    def read_partition(self, addr: int, length: int,
                       part_id: int = EMMC_USER,
                       storage_type: int = HW_STORAGE_EMMC,
                       progress_cb: Callable = None) -> bytes:
        """Read data from flash partition."""
        if self.is_xflash and self.xflash:
            return self.xflash.read_data(
                addr, length, storage_type, part_id, progress_cb
            )
        elif self.legacy:
            return self.legacy.read_emmc(
                addr, length, part_id, progress_cb
            )
        else:
            raise DAError("No DA protocol initialized")

    def shutdown(self, reboot: bool = True):
        """Shutdown DA and optionally reboot."""
        if self.is_xflash and self.xflash:
            self.xflash.shutdown(reboot)
        elif self.legacy:
            self.legacy.finish(reboot)
