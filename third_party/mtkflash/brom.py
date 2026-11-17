"""
MTK BROM / Preloader Protocol.

Supports two boot modes:
1. BROM mode: device listens silently, handshake is 0xA0→0x5F...
2. Preloader mode: device sends "READY" repeatedly, need to drain it first

Wire protocol (from mtkclient + libflashtool.so analysis):
  Handshake: 0xA0→0x5F, 0x0A→0xF5, 0x50→0xAF, 0x05→0xFA
  Commands:  send cmd(1) → read echo(1, same byte) → read response
  Parameters: send u32 BE → read echo(4, same bytes)
"""

import struct
import time
from typing import Tuple


CHIP_NAMES = {
    0x0321: "MT6735", 0x0335: "MT6737", 0x0337: "MT6737M",
    0x0326: "MT6750", 0x0551: "MT6750S", 0x0279: "MT6755",
    0x0633: "MT6757", 0x0507: "MT6761", 0x0717: "MT6761D",
    0x0690: "MT6762", 0x0766: "MT6763", 0x0707: "MT6765",
    0x0725: "MT6768", 0x0788: "MT6771", 0x0813: "MT6779",
    0x0886: "MT6785", 0x0959: "MT6789",
    0x0996: "MT6833", 0x0950: "MT6853", 0x0870: "MT6873",
    0x0900: "MT6877", 0x0816: "MT6885", 0x0975: "MT6893",
    0x8163: "MT8163", 0x8127: "MT8127", 0x8173: "MT8173",
    0x0928: "MT6983", 0x0988: "MT6895",
}

HANDSHAKE = [(0xA0, 0x5F), (0x0A, 0xF5), (0x50, 0xAF), (0x05, 0xFA)]


class BromError(Exception):
    pass


class DeviceInfo:
    def __init__(self):
        self.hw_code = 0
        self.hw_sub_code = 0
        self.hw_ver = 0
        self.sw_ver = 0
        self.chip_name = ""
        self.target_config = 0
        self.secure_boot = False
        self.serial_link_auth = False
        self.daa = False
        self.preloader_mode = False

    def __str__(self):
        mode = "Preloader" if self.preloader_mode else "BROM"
        return (
            f"Chip: {self.chip_name} (HW={self.hw_code:#06x}) [{mode}]\n"
            f"HW Ver: {self.hw_ver:#06x}, SW Ver: {self.sw_ver:#06x}\n"
            f"Secure Boot: {self.secure_boot}, SLA: {self.serial_link_auth}"
        )


class BromProtocol:
    def __init__(self, conn):
        self.conn = conn
        self.device_info = DeviceInfo()

    # ==========================================================
    # Low-level I/O
    # ==========================================================

    def _write(self, data: bytes):
        self.conn.write(data)

    def _read(self, n: int, timeout_ms: int = 1000) -> bytes:
        return self.conn.read(n, timeout_ms=timeout_ms)

    def _read_exact(self, n: int, timeout_ms: int = 5000) -> bytes:
        return self.conn.read_exact(n, timeout_ms=timeout_ms)

    def _drain(self):
        """Drain all pending RX data (including READY spam)."""
        drained = 0
        while True:
            try:
                d = self._read(512, timeout_ms=50)
                if not d or len(d) == 0:
                    break
                drained += len(d)
            except Exception:
                break
        if drained > 0:
            print(f"[BROM] Drained {drained} bytes")

    def _echo(self, byte: int) -> bool:
        """Send byte, expect exact same byte back (command echo)."""
        self._write(bytes([byte]))
        resp = self._read(1, timeout_ms=1000)
        if len(resp) == 0:
            raise BromError(f"No echo for 0x{byte:02X}")
        if resp[0] != byte:
            raise BromError(f"Echo mismatch: sent 0x{byte:02X} got 0x{resp[0]:02X}")
        return True

    def _send_u32(self, val: int):
        """Send u32 big-endian, consume echo if device sends one."""
        data = struct.pack(">I", val)
        self._write(data)
        # Try to read echo - some preloaders echo, some don't
        try:
            resp = self._read(4, timeout_ms=300)
            # Got something back - could be echo or response data
            # If it matches what we sent, it's an echo (consumed)
            # If not, we'll deal with it in the caller
        except Exception:
            pass  # No echo, that's OK

    def _recv_u16(self) -> int:
        return struct.unpack(">H", self._read_exact(2))[0]

    def _recv_u32(self) -> int:
        return struct.unpack(">I", self._read_exact(4))[0]

    # ==========================================================
    # Handshake
    # ==========================================================

    def handshake(self, timeout_sec: float = 10.0) -> bool:
        """
        Aggressive handshake that works with READY-spamming preloader.

        Strategy: keep hammering 0xA0 while reading responses.
        When 0x5F appears (even mixed with READY bytes), the device
        heard us. Then quickly send remaining 3 bytes.
        """
        print("[BROM] Handshake...")
        start = time.time()
        ready_seen = False

        while time.time() - start < timeout_sec:
            # Phase 1: Blast 0xA0 and hunt for 0x5F in response
            self._write(bytes([0xA0]))
            try:
                resp = self._read(64, timeout_ms=50)
            except Exception:
                resp = b""

            if not resp:
                time.sleep(0.01)
                continue

            # Check for READY (preloader mode)
            if b"READY" in resp and not ready_seen:
                print("[BROM] Preloader detected (READY)")
                ready_seen = True
                self.device_info.preloader_mode = True

            # Look for 0x5F anywhere in the response
            if 0x5F not in resp:
                continue

            # Found 0x5F! Device responded to our 0xA0.
            # Now send remaining handshake bytes quickly.
            print("[BROM] Got 0x5F, completing handshake...")

            remaining = HANDSHAKE[1:]  # 0x0A→0xF5, 0x50→0xAF, 0x05→0xFA
            ok = True
            for send_b, expect_b in remaining:
                self._write(bytes([send_b]))
                try:
                    r = self._read(1, timeout_ms=500)
                except Exception:
                    r = b""

                if len(r) == 0 or r[0] != expect_b:
                    print(f"[BROM] Byte 0x{send_b:02X}: expected 0x{expect_b:02X}, "
                          f"got {f'0x{r[0]:02X}' if r else 'nothing'}")
                    ok = False
                    break

            if ok:
                elapsed = time.time() - start
                mode = "Preloader" if self.device_info.preloader_mode else "BROM"
                print(f"[BROM] Handshake OK ({mode}, {elapsed:.2f}s)")
                return True

            # Failed to complete - retry
            self._drain()

        raise BromError("Handshake timeout")

    # ==========================================================
    # BROM/Preloader commands
    # Protocol: echo(cmd) → response data (format per command)
    # ==========================================================

    def _try_recv_u16(self, timeout_ms=300) -> int:
        """Try to read 2-byte status, return 0 if not available."""
        try:
            data = self._read(2, timeout_ms=timeout_ms)
            if len(data) == 2:
                return struct.unpack(">H", data)[0]
        except Exception:
            pass
        return 0

    def get_hw_code(self) -> int:
        """
        GET_HW_CODE (0xFD):
          echo(0xFD) → 4 bytes (hw_code:16 + hw_sub:16) → optional status(2)
        """
        self._echo(0xFD)
        val = self._recv_u32()
        self._try_recv_u16()  # trailing status - optional on some chips

        hw_code = (val >> 16) & 0xFFFF
        hw_sub = val & 0xFFFF

        self.device_info.hw_code = hw_code
        self.device_info.hw_sub_code = hw_sub
        self.device_info.chip_name = CHIP_NAMES.get(hw_code, f"MT{hw_code:04X}")
        return hw_code

    def get_hw_sw_ver(self) -> Tuple[int, int, int]:
        """
        GET_HW_SW_VER (0xFC):
          echo(0xFC) → hw_sub(2) + hw_ver(2) + sw_ver(2) → optional status(2)
        """
        self._echo(0xFC)
        hw_sub = self._recv_u16()
        hw_ver = self._recv_u16()
        sw_ver = self._recv_u16()
        self._try_recv_u16()  # trailing status - optional

        self.device_info.hw_sub_code = hw_sub
        self.device_info.hw_ver = hw_ver
        self.device_info.sw_ver = sw_ver
        return hw_sub, hw_ver, sw_ver

    def get_target_config(self) -> int:
        """
        GET_TARGET_CONFIG (0xD8):
          echo(0xD8) → config(4) → optional status(2)
        """
        self._echo(0xD8)
        cfg = self._recv_u32()
        self._try_recv_u16()  # trailing status - optional

        self.device_info.target_config = cfg
        self.device_info.secure_boot = bool(cfg & 1)
        self.device_info.serial_link_auth = bool(cfg & 2)
        self.device_info.daa = bool(cfg & 4)
        return cfg

    # ==========================================================
    # DA upload (Brom_DownloadData_Ex)
    # ==========================================================

    def send_da(self, addr: int, data: bytes, sig_len: int = 0):
        """
        SEND_DA (0xD7):
          echo(cmd) → send params → recv status → send data → recv checksum + status
        """
        length = len(data)
        print(f"[BROM] Uploading DA ({length} bytes) → {addr:#010x}")

        self._echo(0xD7)
        self._send_u32(addr)
        self._send_u32(length)
        self._send_u32(sig_len)

        # Read status - might need to skip echo bytes first
        # Read up to 10 bytes and find the status
        raw = b""
        try:
            raw = self._read(16, timeout_ms=2000)
        except Exception:
            pass

        if not raw:
            raise BromError("SEND_DA: no response after parameters")

        print(f"[BROM] SEND_DA response: {raw.hex()}")

        # Look for status - last 2 bytes, or scan for non-echo data
        # Status 0x0000 = OK, anything else = error
        # For SLA-required devices: 0x1D0D
        status = struct.unpack(">H", raw[-2:])[0] if len(raw) >= 2 else 0xFFFF
        if status != 0:
            # Maybe the status is embedded differently - try all 2-byte windows
            found_ok = False
            for i in range(len(raw) - 1):
                val = struct.unpack(">H", raw[i:i+2])[0]
                if val == 0:
                    found_ok = True
                    break
            if not found_ok:
                raise BromError(f"SEND_DA rejected: response={raw.hex()}")

        # Send data in chunks
        sent = 0
        while sent < length:
            end = min(sent + 0x1000, length)  # 4KB chunks
            self._write(data[sent:end])
            sent = end
            if sent % 0x20000 == 0 or sent == length:
                print(f"\r[BROM] DA: {sent*100//length}%", end="", flush=True)
        print()

        # Checksum and final status
        final_raw = self._read(4, timeout_ms=10000)
        if len(final_raw) >= 4:
            dev_checksum = struct.unpack(">H", final_raw[0:2])[0]
            final_status = struct.unpack(">H", final_raw[2:4])[0]
        elif len(final_raw) >= 2:
            dev_checksum = struct.unpack(">H", final_raw[0:2])[0]
            final_status = 0
        else:
            dev_checksum = 0
            final_status = 0

        print(f"[BROM] DA upload done (checksum={dev_checksum:#06x}, status={final_status:#06x})")

        if final_status != 0:
            raise BromError(f"DA upload failed: status={final_status:#06x}")

    def jump_da(self, addr: int):
        """
        JUMP_DA (0xD5):
          echo(cmd) → send addr → recv response
        """
        print(f"[BROM] Jump → {addr:#010x}")
        self._echo(0xD5)
        self._send_u32(addr)

        # Read response - might be addr echo + status, or just status
        resp = self._read(8, timeout_ms=3000)
        print(f"[BROM] JUMP response: {resp.hex() if resp else 'empty'}")

        # Check last 2 bytes for status
        if len(resp) >= 2:
            status = struct.unpack(">H", resp[-2:])[0]
            if status != 0 and status != addr & 0xFFFF:
                print(f"[BROM] JUMP status: {status:#06x}")

        print("[BROM] DA started")
        time.sleep(0.2)

    # ==========================================================
    # Full init
    # ==========================================================

    def init_device(self, max_handshake_retries: int = 3) -> DeviceInfo:
        last_err = None
        for i in range(max_handshake_retries):
            try:
                self.handshake(timeout_sec=8)
                last_err = None
                break
            except BromError as e:
                last_err = e
                print(f"[BROM] Attempt {i+1}: {e}")
                time.sleep(0.05)

        if last_err:
            raise last_err

        hw = self.get_hw_code()
        print(f"[BROM] Chip: {self.device_info.chip_name} ({hw:#06x})")

        sub, hv, sv = self.get_hw_sw_ver()
        print(f"[BROM] HW={hv:#06x} SW={sv:#06x}")

        try:
            self.get_target_config()
            print(f"[BROM] SBC={self.device_info.secure_boot} "
                  f"SLA={self.device_info.serial_link_auth}")
        except Exception:
            print("[BROM] Target config N/A")

        return self.device_info

    def upload_and_start_da(self, da_path: str, da_addr: int = 0x200000):
        with open(da_path, "rb") as f:
            da_data = f.read()
        self.send_da(da_addr, da_data)
        self.jump_da(da_addr)
        time.sleep(0.5)
