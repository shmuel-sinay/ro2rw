"""
Serial (CDC ACM) Communication Layer for MTK devices on macOS.
Uses pyserial to talk to the device via /dev/cu.usbmodem* - exactly
how SP Flash Tool communicates via COM ports.
"""

import serial
import serial.tools.list_ports
import time
import struct
import glob
from typing import Optional


MTK_VID = 0x0E8D
MTK_PIDS = {0x0003, 0x2000, 0x2001, 0x20FF}


class SerialError(Exception):
    pass


class SerialConnection:
    """Serial port communication with MTK devices via CDC ACM."""

    BAUD = 115200  # BROM default, upgraded later
    TIMEOUT = 1.0

    def __init__(self):
        self.port: Optional[serial.Serial] = None
        self.port_name: str = ""
        self._connected = False

    def find_device(self, timeout_sec: float = 60.0) -> bool:
        """Wait for MTK serial device to appear."""
        print(f"[SERIAL] Waiting for MTK device (timeout: {timeout_sec}s)...")
        start = time.time()

        while time.time() - start < timeout_sec:
            # Method 1: pyserial port enumeration (checks VID/PID)
            for p in serial.tools.list_ports.comports():
                if p.vid == MTK_VID and (p.pid in MTK_PIDS or True):
                    self.port_name = p.device
                    print(f"[SERIAL] Found: {p.device} "
                          f"(VID={p.vid:#06x} PID={p.pid:#06x} - {p.description})")
                    return True

            # Method 2: glob for macOS usbmodem devices
            for pattern in ["/dev/cu.usbmodem*", "/dev/tty.usbmodem*"]:
                matches = glob.glob(pattern)
                for dev in matches:
                    self.port_name = dev
                    print(f"[SERIAL] Found: {dev}")
                    return True

            time.sleep(0.05)  # 50ms poll - fast for bootloop

        print("[SERIAL] Timeout - no MTK device found")
        return False

    def connect(self, baud: int = None) -> bool:
        """Open serial port."""
        if not self.port_name:
            raise SerialError("No device found")

        if baud is None:
            baud = self.BAUD

        try:
            self.port = serial.Serial(
                port=self.port_name,
                baudrate=baud,
                bytesize=serial.EIGHTBITS,
                parity=serial.PARITY_NONE,
                stopbits=serial.STOPBITS_ONE,
                timeout=self.TIMEOUT,
                write_timeout=self.TIMEOUT,
                xonxoff=False,
                rtscts=False,
                dsrdtr=False,
            )

            # Clear buffers
            self.port.reset_input_buffer()
            self.port.reset_output_buffer()

            self._connected = True
            print(f"[SERIAL] Connected: {self.port_name} @ {baud} baud")
            return True

        except serial.SerialException as e:
            raise SerialError(f"Failed to open {self.port_name}: {e}")

    def find_and_connect(self, timeout_sec: float = 120.0, baud: int = None) -> bool:
        """
        Combined find+connect with auto-retry for bootloop devices.
        Keeps trying until successful or timeout.
        """
        print(f"[SERIAL] Auto-connect mode ({timeout_sec}s timeout)...")
        start = time.time()
        attempt = 0

        while time.time() - start < timeout_sec:
            # Find device
            self.port_name = ""
            found = False

            # Quick scan
            for p in serial.tools.list_ports.comports():
                if p.vid == MTK_VID:
                    self.port_name = p.device
                    found = True
                    break

            if not found:
                for pattern in ["/dev/cu.usbmodem*", "/dev/tty.usbmodem*"]:
                    matches = glob.glob(pattern)
                    if matches:
                        self.port_name = matches[0]
                        found = True
                        break

            if not found:
                time.sleep(0.05)
                continue

            # Try to connect
            attempt += 1
            try:
                self.connect(baud)
                return True
            except SerialError as e:
                print(f"[SERIAL] Attempt {attempt}: {e}")
                self.disconnect()
                time.sleep(0.3)

        print("[SERIAL] Timeout")
        return False

    def write(self, data: bytes, timeout_ms: int = None) -> int:
        """Write data to device."""
        if not self._connected or not self.port:
            raise SerialError("Not connected")
        try:
            return self.port.write(data)
        except serial.SerialException as e:
            raise SerialError(f"Write error: {e}")

    def read(self, length: int, timeout_ms: int = None) -> bytes:
        """Read data from device."""
        if not self._connected or not self.port:
            raise SerialError("Not connected")

        if timeout_ms is not None:
            old_timeout = self.port.timeout
            self.port.timeout = timeout_ms / 1000.0

        try:
            data = self.port.read(length)
            return data
        except serial.SerialException as e:
            raise SerialError(f"Read error: {e}")
        finally:
            if timeout_ms is not None:
                self.port.timeout = old_timeout

    def read_exact(self, length: int, timeout_ms: int = None) -> bytes:
        """Read exactly N bytes."""
        if timeout_ms is None:
            timeout_ms = 5000

        result = bytearray()
        deadline = time.time() + (timeout_ms / 1000.0)

        while len(result) < length:
            if time.time() > deadline:
                raise SerialError(
                    f"Read timeout: got {len(result)}/{length} bytes")
            remaining = length - len(result)
            chunk = self.read(remaining, timeout_ms=min(1000, timeout_ms))
            if chunk:
                result.extend(chunk)
            else:
                time.sleep(0.001)

        return bytes(result)

    def write_bulk(self, data: bytes, chunk_size: int = 0x100000,
                   progress_cb=None, timeout_ms: int = 30000) -> int:
        """Write large data in chunks with progress."""
        total = len(data)
        sent = 0

        while sent < total:
            end = min(sent + chunk_size, total)
            chunk = data[sent:end]
            self.write(chunk)
            sent = end
            if progress_cb:
                progress_cb(sent, total)

        return sent

    def set_baudrate(self, baud: int):
        """Change baud rate."""
        if self.port:
            self.port.baudrate = baud
            print(f"[SERIAL] Baud rate changed to {baud}")

    def flush(self):
        """Flush buffers."""
        if self.port:
            self.port.flush()
            self.port.reset_input_buffer()

    def disconnect(self):
        """Close serial port."""
        if self.port:
            try:
                self.port.close()
            except Exception:
                pass
        self.port = None
        self._connected = False
        self.port_name = ""

    def reconnect(self, timeout_sec: float = 30.0) -> bool:
        """Wait for device to re-enumerate after DA jump."""
        print("[SERIAL] Waiting for device re-enumeration...")
        self.disconnect()
        time.sleep(1)
        return self.find_and_connect(timeout_sec)

    @property
    def connected(self) -> bool:
        return self._connected and self.port is not None and self.port.is_open
