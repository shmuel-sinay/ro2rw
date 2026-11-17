"""
USB Communication Layer for MTK devices on macOS.
Uses pyusb (libusb backend) for direct USB communication.
Optimized for high-throughput bulk transfers.
"""

import usb.core
import usb.util
import usb.backend.libusb1
import time
import sys
import struct
from typing import Optional, Tuple


# MTK USB IDs
MTK_VID = 0x0E8D
MTK_BROM_PID_LIST = [0x0003, 0x2000, 0x2001, 0x20FF]
MTK_PRELOADER_PID_LIST = [0x2000, 0x2001]
MTK_DA_PID_LIST = [0x2001, 0x20FF, 0x0003]


class USBError(Exception):
    pass


class USBConnection:
    """Low-level USB communication with MTK devices."""

    TIMEOUT_MS = 5000
    MAX_RETRIES = 3

    def __init__(self):
        self.device: Optional[usb.core.Device] = None
        self.ep_in: Optional[usb.core.Endpoint] = None
        self.ep_out: Optional[usb.core.Endpoint] = None
        self.ep_in_addr = 0x81
        self.ep_out_addr = 0x02
        self.max_in_size = 512
        self.max_out_size = 512
        self._connected = False

    def find_device(self, timeout_sec: float = 60.0) -> bool:
        """Wait for MTK device to appear on USB bus.
        Optimized for bootloop devices - scans rapidly."""
        print(f"[USB] Waiting for MTK device (timeout: {timeout_sec}s)...")
        print("[USB] For bootloop device: keep it connected, tool will catch it")

        start = time.time()
        all_pids = set(MTK_BROM_PID_LIST + MTK_PRELOADER_PID_LIST + MTK_DA_PID_LIST)

        while time.time() - start < timeout_sec:
            # Scan ALL USB devices at once (faster than per-PID search)
            try:
                all_devs = usb.core.find(find_all=True, idVendor=MTK_VID)
                for dev in all_devs:
                    if dev.idProduct in all_pids:
                        self.device = dev
                        print(f"[USB] Found device: VID={MTK_VID:#06x} "
                              f"PID={dev.idProduct:#06x}")
                        return True
            except usb.core.USBError:
                pass

            # Also try without vendor filter (some BROM modes use different VID)
            try:
                for dev in usb.core.find(find_all=True):
                    if dev.idVendor == MTK_VID and dev.idProduct in all_pids:
                        self.device = dev
                        print(f"[USB] Found device: VID={MTK_VID:#06x} "
                              f"PID={dev.idProduct:#06x}")
                        return True
            except usb.core.USBError:
                pass

            time.sleep(0.02)  # 20ms poll - fast enough to catch bootloop window

        print("[USB] Timeout - no MTK device found")
        return False

    def find_and_connect(self, timeout_sec: float = 120.0) -> bool:
        """
        Combined find+connect with auto-retry for bootloop devices.
        Keeps trying until successful connection or timeout.
        """
        print(f"[USB] Waiting for device (auto-retry mode, {timeout_sec}s)...")
        print("[USB] Device in bootloop? Keep it connected - will catch the window")

        start = time.time()
        all_pids = set(MTK_BROM_PID_LIST + MTK_PRELOADER_PID_LIST + MTK_DA_PID_LIST)
        attempt = 0

        while time.time() - start < timeout_sec:
            # Fast scan
            dev = None
            try:
                for d in usb.core.find(find_all=True):
                    if d.idVendor == MTK_VID and d.idProduct in all_pids:
                        dev = d
                        break
            except usb.core.USBError:
                time.sleep(0.02)
                continue

            if dev is None:
                time.sleep(0.02)
                continue

            # Found device - immediately try to connect
            attempt += 1
            self.device = dev
            print(f"[USB] Found device (attempt {attempt}): "
                  f"PID={dev.idProduct:#06x}")

            try:
                self._fast_connect()
                return True
            except (USBError, usb.core.USBError) as e:
                print(f"[USB] Connect attempt {attempt} failed: {e}")
                print("[USB] Device probably rebooted - waiting for next window...")
                self.device = None
                self.ep_in = None
                self.ep_out = None
                self._connected = False
                time.sleep(0.5)  # Wait for reboot cycle
                continue

        print("[USB] Timeout")
        return False

    def _fast_connect(self):
        """Minimal-delay connection - grab endpoints ASAP."""
        dev = self.device

        # Detach kernel driver
        try:
            for cfg in dev:
                for intf in cfg:
                    try:
                        if dev.is_kernel_driver_active(intf.bInterfaceNumber):
                            dev.detach_kernel_driver(intf.bInterfaceNumber)
                    except (usb.core.USBError, NotImplementedError):
                        pass
        except usb.core.USBError:
            pass

        # Set configuration
        try:
            dev.set_configuration()
        except usb.core.USBError:
            pass

        # Search ALL interfaces for bulk endpoints (not just 0,0)
        cfg = dev.get_active_configuration()
        ep_out = None
        ep_in = None

        for intf in cfg:
            for ep in intf:
                if usb.util.endpoint_direction(ep.bEndpointAddress) == usb.util.ENDPOINT_OUT:
                    if ep_out is None:
                        ep_out = ep
                elif usb.util.endpoint_direction(ep.bEndpointAddress) == usb.util.ENDPOINT_IN:
                    if ep_in is None:
                        ep_in = ep
            if ep_out and ep_in:
                break

        if ep_out is None or ep_in is None:
            raise USBError(f"No bulk endpoints (found OUT={ep_out is not None}, "
                           f"IN={ep_in is not None}, "
                           f"interfaces={cfg.bNumInterfaces})")

        self.ep_out = ep_out
        self.ep_in = ep_in
        self.ep_in_addr = ep_in.bEndpointAddress
        self.ep_out_addr = ep_out.bEndpointAddress
        self.max_in_size = ep_in.wMaxPacketSize
        self.max_out_size = ep_out.wMaxPacketSize
        self._connected = True

        print(f"[USB] Connected! EP_OUT={self.ep_out_addr:#04x} "
              f"EP_IN={self.ep_in_addr:#04x}")

    def connect(self) -> bool:
        """Claim USB interface and configure endpoints."""
        if self.device is None:
            raise USBError("No device found")

        try:
            self._fast_connect()

            self._connected = True
            return True

        except usb.core.USBError as e:
            raise USBError(f"USB connection failed: {e}")

    def write(self, data: bytes, timeout_ms: int = None) -> int:
        """Write data to device. Returns bytes written."""
        if not self._connected:
            raise USBError("Not connected")
        if timeout_ms is None:
            timeout_ms = self.TIMEOUT_MS

        total = 0
        offset = 0
        while offset < len(data):
            chunk = data[offset:]
            try:
                written = self.ep_out.write(chunk, timeout=timeout_ms)
                offset += written
                total += written
            except usb.core.USBError as e:
                if "timeout" in str(e).lower():
                    raise USBError(f"Write timeout after {total} bytes")
                raise USBError(f"Write error: {e}")

        return total

    def read(self, length: int, timeout_ms: int = None) -> bytes:
        """Read data from device."""
        if not self._connected:
            raise USBError("Not connected")
        if timeout_ms is None:
            timeout_ms = self.TIMEOUT_MS

        try:
            data = self.ep_in.read(length, timeout=timeout_ms)
            return bytes(data)
        except usb.core.USBError as e:
            if "timeout" in str(e).lower():
                raise USBError(f"Read timeout (expected {length} bytes)")
            raise USBError(f"Read error: {e}")

    def read_exact(self, length: int, timeout_ms: int = None) -> bytes:
        """Read exact number of bytes, retrying if needed."""
        if timeout_ms is None:
            timeout_ms = self.TIMEOUT_MS

        result = bytearray()
        remaining = length
        deadline = time.time() + (timeout_ms / 1000.0)

        while remaining > 0:
            if time.time() > deadline:
                raise USBError(f"Read timeout: got {len(result)}/{length} bytes")
            try:
                chunk = self.read(remaining, timeout_ms=min(1000, timeout_ms))
                result.extend(chunk)
                remaining -= len(chunk)
            except USBError:
                if remaining == length:
                    raise  # Nothing received at all
                time.sleep(0.01)

        return bytes(result)

    def write_bulk(self, data: bytes, chunk_size: int = 0x100000,
                   progress_cb=None, timeout_ms: int = 30000) -> int:
        """
        High-throughput bulk write - sends data in large chunks.
        This is the key optimization over mtkclient's small packet approach.
        chunk_size: default 1MB (like SP Flash Tool's DRAM_PACKET_SIZE)
        """
        total = len(data)
        sent = 0

        while sent < total:
            end = min(sent + chunk_size, total)
            chunk = data[sent:end]

            # Pad to USB packet boundary for efficiency
            pad_len = (self.max_out_size - (len(chunk) % self.max_out_size)) % self.max_out_size
            if pad_len > 0 and end == total:
                chunk = chunk + b'\x00' * pad_len

            written = self.write(chunk, timeout_ms=timeout_ms)
            sent += (end - sent)

            if progress_cb:
                progress_cb(sent, total)

        return sent

    def read_bulk(self, length: int, chunk_size: int = 0x100000,
                  progress_cb=None, timeout_ms: int = 30000) -> bytes:
        """High-throughput bulk read."""
        result = bytearray()
        received = 0

        while received < length:
            to_read = min(chunk_size, length - received)
            chunk = self.read(to_read, timeout_ms=timeout_ms)
            result.extend(chunk)
            received += len(chunk)

            if progress_cb:
                progress_cb(received, length)

        return bytes(result)

    def ctrl_transfer(self, bmRequestType, bRequest, wValue=0,
                      wIndex=0, data_or_wLength=None, timeout_ms=None):
        """USB control transfer."""
        if timeout_ms is None:
            timeout_ms = self.TIMEOUT_MS
        return self.device.ctrl_transfer(
            bmRequestType, bRequest, wValue, wIndex,
            data_or_wLength, timeout=timeout_ms
        )

    def set_line_coding(self, baudrate: int = 921600):
        """Set CDC line coding (virtual COM port baud rate)."""
        # CDC SET_LINE_CODING request
        data = struct.pack("<IBBB", baudrate, 0, 0, 8)  # baud, stop, parity, bits
        try:
            self.ctrl_transfer(0x21, 0x20, 0, 0, data)
        except USBError:
            pass  # Some devices don't support this

    def reconnect(self, timeout_sec: float = 30.0) -> bool:
        """Wait for device to re-enumerate (after DA upload, speed change, etc.)."""
        print("[USB] Waiting for device re-enumeration...")
        self.disconnect()
        time.sleep(0.5)

        if self.find_device(timeout_sec):
            return self.connect()
        return False

    def disconnect(self):
        """Release USB interface."""
        if self.device:
            try:
                usb.util.dispose_resources(self.device)
            except Exception:
                pass
        self._connected = False
        self.device = None
        self.ep_in = None
        self.ep_out = None

    @property
    def connected(self) -> bool:
        return self._connected

    def echo_test(self) -> bool:
        """Quick connectivity test - send byte and check echo."""
        try:
            self.write(b"\xA0")
            resp = self.read(1, timeout_ms=1000)
            return len(resp) > 0
        except USBError:
            return False
