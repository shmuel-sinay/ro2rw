"""
Unified MTK device connection - pyusb only, no serial.
Handles BROM handshake, DA upload, XFlash protocol, and flashing.
Mirrors mtkclient's approach: one USB connection from start to finish.
"""

import usb.core
import usb.util
import usb.backend.libusb1
import struct
import time
import os
import sys

MTK_VID = 0x0E8D
XFLASH_MAGIC = 0xFEEEEEEF

CHIP_NAMES = {
    0x0321: "MT6735", 0x0335: "MT6737", 0x0507: "MT6761",
    0x0690: "MT6762", 0x0707: "MT6765", 0x0717: "MT6761D",
    0x0725: "MT6768", 0x0788: "MT6771", 0x0813: "MT6779",
    0x0886: "MT6785", 0x0959: "MT6789", 0x0996: "MT6833",
}


class MTKError(Exception):
    pass


class MTKDevice:
    """Single USB connection for entire BROM → DA → Flash lifecycle."""

    def __init__(self, log_cb=None):
        self.dev = None
        self.ep_out = None
        self.ep_in = None
        # Forensic logger: tees every log line to a timestamped file and keeps
        # per-partition write/verify records so a "successful" flash that still
        # bricks can be diagnosed from the log alone.
        try:
            from flash_logger import FlashLogger
            self.flog = FlashLogger(log_cb)
            self.log = self.flog  # FlashLogger is callable as log(level, msg)
        except Exception:
            self.flog = None
            self.log = log_cb or (lambda level, msg: print(f"[{level}] {msg}"))
        self.hw_code = 0
        self.chip_name = ""
        # Write chunk size MUST match what the DA negotiates (queried in
        # full_init). A mismatch desyncs the data stream → DA reports OK but
        # writes corrupted bytes → silent brick. 0x200000 is a safe fallback.
        self.write_packet_size = 0x200000

    # ==========================================================
    # USB I/O
    # ==========================================================

    def _write(self, data):
        self.ep_out.write(data, timeout=5000)

    def _read(self, size=64, timeout=1000):
        try:
            # Always read at least 512 to avoid overflow on bulk endpoints
            read_size = max(size, 512)
            data = bytes(self.ep_in.read(read_size, timeout=timeout))
            return data[:size] if len(data) > size else data
        except usb.core.USBTimeoutError:
            return b""
        except usb.core.USBError as e:
            if "timeout" in str(e).lower() or "timed out" in str(e).lower():
                return b""
            if "overflow" in str(e).lower():
                # Retry with larger buffer
                try:
                    return bytes(self.ep_in.read(16384, timeout=timeout))
                except Exception:
                    return b""
            raise

    def _read_exact(self, size, timeout=5000):
        result = bytearray()
        deadline = time.time() + timeout / 1000
        while len(result) < size:
            if time.time() > deadline:
                raise MTKError(f"Read timeout: {len(result)}/{size}")
            chunk = self._read(size - len(result), timeout=min(1000, timeout))
            if chunk:
                result.extend(chunk)
            else:
                time.sleep(0.001)
        return bytes(result)

    def _flush(self):
        for _ in range(20):
            d = self._read(512, timeout=10)
            if not d:
                break

    # ==========================================================
    # Connect - find device, claim interface, set CDC
    # ==========================================================

    def connect(self, timeout_sec=60):
        """Find MTK USB device, detach kernel driver, claim interface."""
        self.log("info", "Waiting for MTK device on USB...")
        start = time.time()

        while time.time() - start < timeout_sec:
            self.dev = usb.core.find(idVendor=MTK_VID)
            if self.dev:
                break
            time.sleep(0.05)

        if not self.dev:
            raise MTKError("No MTK USB device found")

        # BROM = 0x0003 (DRAM not yet up). Anything else (0x2000/0x201c/...) is
        # Preloader mode, where the preloader has ALREADY initialised DRAM — so
        # we must skip our EMI/DRAM init (it fails / returns None there). This
        # lets the tool flash devices with a working preloader, not just bricks.
        self.is_preloader = (self.dev.idProduct != 0x0003)
        mode = "Preloader" if self.is_preloader else "BROM"
        self.log("info", f"Found: PID={self.dev.idProduct:#06x} ({mode} mode)")

        # Detach kernel driver from data interface
        try:
            if self.dev.is_kernel_driver_active(0):
                self.dev.detach_kernel_driver(0)
        except Exception:
            pass

        try:
            self.dev.set_configuration()
        except Exception:
            pass

        try:
            usb.util.claim_interface(self.dev, 0)
        except Exception:
            pass

        # Detach kernel driver from ALL interfaces first
        cfg = self.dev.get_active_configuration()
        for intf in cfg:
            try:
                if self.dev.is_kernel_driver_active(intf.bInterfaceNumber):
                    self.dev.detach_kernel_driver(intf.bInterfaceNumber)
            except Exception:
                pass

        # Find bulk endpoints on CDC Data interface
        for intf in cfg:
            ep_o = usb.util.find_descriptor(
                intf, custom_match=lambda e:
                usb.util.endpoint_direction(e.bEndpointAddress) == usb.util.ENDPOINT_OUT)
            ep_i = usb.util.find_descriptor(
                intf, custom_match=lambda e:
                usb.util.endpoint_direction(e.bEndpointAddress) == usb.util.ENDPOINT_IN)
            if ep_o and ep_i:
                self.ep_out = ep_o
                self.ep_in = ep_i
                try:
                    usb.util.claim_interface(self.dev, intf.bInterfaceNumber)
                except Exception:
                    pass
                self.log("info", f"Using intf {intf.bInterfaceNumber}: "
                         f"EP {ep_o.bEndpointAddress:#x}/{ep_i.bEndpointAddress:#x}")
                break

        if not self.ep_out or not self.ep_in:
            raise MTKError("No bulk endpoints found")

        # CDC setup
        try:
            self.dev.ctrl_transfer(0x21, 0x20, 0, 0,
                                   struct.pack("<IBBB", 921600, 0, 0, 8))
            self.dev.ctrl_transfer(0x21, 0x22, 0x0003, 0)
        except Exception:
            pass

        self.log("success", f"USB connected: EP {self.ep_out.bEndpointAddress:#x}/{self.ep_in.bEndpointAddress:#x}")

    def disconnect(self):
        if self.dev:
            try:
                usb.util.dispose_resources(self.dev)
            except Exception:
                pass
        self.dev = None

    # ==========================================================
    # BROM Handshake (handles READY from preloader)
    # ==========================================================

    def brom_handshake(self, timeout_sec=10):
        """4-byte handshake, handles READY spam from preloader."""
        self.log("info", "BROM handshake...")
        start = time.time()
        handshake = [(0xA0, 0x5F), (0x0A, 0xF5), (0x50, 0xAF), (0x05, 0xFA)]

        while time.time() - start < timeout_sec:
            # Send 0xA0 and look for 0x5F
            self._write(bytes([0xA0]))
            resp = self._read(64, timeout=100)

            if not resp:
                continue

            if 0x5F in resp:
                # Complete remaining 3 bytes
                ok = True
                for send_b, expect_b in handshake[1:]:
                    self._write(bytes([send_b]))
                    r = self._read(1, timeout=500)
                    if not r or r[0] != expect_b:
                        ok = False
                        break
                if ok:
                    self.log("success", f"Handshake OK ({time.time()-start:.1f}s)")
                    return True
                self._flush()

            time.sleep(0.02)

        raise MTKError("Handshake timeout")

    # ==========================================================
    # BROM Commands
    # ==========================================================

    def _echo(self, byte):
        self._write(bytes([byte]))
        r = self._read_exact(1, timeout=2000)
        if r[0] != byte:
            raise MTKError(f"Echo mismatch: {byte:#x} != {r[0]:#x}")

    def _echo32(self, val):
        data = struct.pack(">I", val)
        self._write(data)
        r = self._read_exact(4, timeout=2000)
        if r != data:
            raise MTKError(f"Echo32 mismatch")

    # ==========================================================
    # Kamakiri BROM exploit (patches BROM to allow DA operations)
    # ==========================================================

    def _brom_reg_read(self, addr, count=1):
        """BROM register read (cmd 0xD1)."""
        self._echo(0xD1)
        self._echo32(addr)
        self._echo32(count)
        status = struct.unpack(">H", self._read_exact(2))[0]
        data = self._read_exact(count * 4)
        struct.unpack(">H", self._read_exact(2))[0]  # trailing status
        return data

    def _brom_reg_write(self, addr, values):
        """BROM register write (cmd 0xD4)."""
        self._echo(0xD4)
        self._echo32(addr)
        self._echo32(len(values))
        struct.unpack(">H", self._read_exact(2))[0]  # status
        for v in values:
            self._write(struct.pack(">I", v))
        struct.unpack(">H", self._read_exact(2))[0]  # trailing status

    def _brom_checksum(self, addr, length, mode=0):
        """BROM checksum cmd 0xDA - used for exploit."""
        self._echo(0xDA)
        self._echo32(addr)
        self._echo32(length)
        self._echo32(mode)
        status = struct.unpack(">H", self._read_exact(2))[0]
        if mode == 0 and length == 0:
            # Read result byte + trailing status
            result = self._read_exact(1)
            struct.unpack(">H", self._read_exact(2))[0]
            return result
        return bytes([status & 0xFF])

    def kamakiri_exploit(self):
        """Run Kamakiri BROM exploit to bypass security restrictions."""
        payload_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                     "mt6761_payload.bin")
        if not os.path.exists(payload_path):
            self.log("warning", "No Kamakiri payload found, skipping exploit")
            return False

        with open(payload_path, "rb") as f:
            payload = f.read()

        self.log("info", f"Kamakiri exploit ({len(payload)} bytes)...")

        try:
            # Step 1: Disable watchdog
            self.log("info", "  Disabling WDT...")
            self._brom_reg_write(0x10007000, [0x22000064])

            # Step 2: Read WDT status
            self._brom_reg_read(0x10007050, 1)

            # Step 3: Upload payload to SRAM
            payload_addr = 0x100A00
            self.log("info", f"  Uploading payload to {payload_addr:#x}...")
            self.send_da(payload_addr, payload, 0)

            # Step 4: Write jump address
            jump_ptr = 0x10282C  # BROM jump table entry
            self._brom_reg_write(jump_ptr, [payload_addr])

            # Step 5: Disable WDT again
            self._brom_reg_read(0x10007050, 1)

            # Step 6: Trigger payload via checksum cmd
            self.log("info", "  Triggering payload...")
            self._write(struct.pack(">I", 0x000A1000))
            result = self._read_exact(4, timeout=5000)
            if result == b'\xa1\xa2\xa3\xa4':
                self.log("success", "Kamakiri exploit SUCCESS!")
                return True
            else:
                self.log("warning", f"  Payload response: {result.hex()}")
                return False

        except Exception as e:
            self.log("warning", f"  Kamakiri: {e}")
            return False

    def get_hw_code(self):
        self._echo(0xFD)
        val = struct.unpack(">I", self._read_exact(4))[0]
        try:
            struct.unpack(">H", self._read_exact(2, timeout=300))
        except Exception:
            pass
        self.hw_code = (val >> 16) & 0xFFFF
        self.chip_name = CHIP_NAMES.get(self.hw_code, f"MT{self.hw_code:04X}")
        self.log("info", f"Chip: {self.chip_name} ({self.hw_code:#06x})")
        return self.hw_code

    def get_hw_sw_ver(self):
        self._echo(0xFC)
        data = self._read_exact(6)
        try:
            self._read_exact(2, timeout=300)
        except Exception:
            pass
        hw_sub, hw_ver, sw_ver = struct.unpack(">HHH", data)
        self.log("info", f"HW={hw_ver:#06x} SW={sw_ver:#06x}")
        return hw_sub, hw_ver, sw_ver

    def get_target_config(self):
        self._echo(0xD8)
        cfg = struct.unpack(">I", self._read_exact(4))[0]
        try:
            self._read_exact(2, timeout=300)
        except Exception:
            pass
        sbc = bool(cfg & 1)
        sla = bool(cfg & 2)
        self.log("info", f"SBC={sbc} SLA={sla}")
        return cfg

    # ==========================================================
    # DA Upload (BROM phase)
    # ==========================================================

    def send_da(self, addr, data, sig_len=0):
        self.log("info", f"SEND_DA: {len(data)} bytes → {addr:#x}")
        self._echo(0xD7)
        self._echo32(addr)
        self._echo32(len(data))
        self._echo32(sig_len)

        # Read status (2 bytes)
        status = self._read_exact(2, timeout=3000)
        self.log("info", f"  status: {status.hex()}")

        # Upload data
        sent = 0
        while sent < len(data):
            end = min(sent + 4096, len(data))
            self._write(data[sent:end])
            sent = end

        # Read checksum (2 bytes) + final status (2 bytes)
        checksum = self._read_exact(2, timeout=10000)
        final_status = self._read_exact(2, timeout=3000)
        self.log("info", f"  checksum: {checksum.hex()}, status: {final_status.hex()}")

        # Drain any extra bytes
        self._flush()

    def jump_da(self, addr):
        self.log("info", f"JUMP_DA → {addr:#x}")
        self._echo(0xD5)
        self._echo32(addr)
        # Read large buffer - DA1 may send lots of data after jump
        resp = self._read(512, timeout=3000)
        self.log("info", f"  resp ({len(resp or b'')}b): [{' '.join(f'{b:02X}' for b in (resp or b'')[:16])}]")
        time.sleep(0.1)

    # ==========================================================
    # XFlash Protocol (DA phase - same USB connection!)
    # ==========================================================

    def xsend(self, cmd_val, read_ack=False):
        """Send XFlash command. Optionally read ack status."""
        payload = struct.pack("<I", cmd_val)
        header = struct.pack("<III", XFLASH_MAGIC, 1, len(payload))
        self._write(header)
        self._write(payload)
        if read_ack:
            return self.xstatus(2000)
        return None

    def xsend_data(self, data):
        """Send XFlash data: header(12) then data as SEPARATE writes."""
        header = struct.pack("<III", XFLASH_MAGIC, 1, len(data))
        self._write(header)
        self._write(data)

    def xread(self, timeout=2000):
        """Read complete XFlash response (header + payload)."""
        result = bytearray()
        deadline = time.time() + timeout / 1000
        while time.time() < deadline:
            chunk = self._read(512, timeout=min(500, int((deadline - time.time()) * 1000)))
            if chunk:
                result.extend(chunk)
                # Check for complete XFlash packet
                if len(result) >= 12:
                    magic = struct.unpack_from("<I", result, 0)[0]
                    if magic == XFLASH_MAGIC:
                        length = struct.unpack_from("<I", result, 8)[0]
                        if len(result) >= 12 + length:
                            return bytes(result[:12 + length])
                    else:
                        # Not XFlash magic - return raw data
                        return bytes(result)
            elif result:
                # Had data but no more coming
                return bytes(result)
        return bytes(result) if result else b""

    def xstatus(self, timeout=2000):
        r = self.xread(timeout)
        if r and len(r) >= 16:
            magic = struct.unpack_from("<I", r, 0)[0]
            if magic == XFLASH_MAGIC:
                length = struct.unpack_from("<I", r, 8)[0]
                if length >= 4:
                    return struct.unpack_from("<I", r, 12)[0]
        return None

    def da1_sync(self):
        """Wait for DA1 0xC0 sync byte, then XFlash handshake."""
        self.log("info", "Waiting for DA1 sync (0xC0)...")
        sync = self._read(1, timeout=5000)
        if sync and sync[0] == 0xC0:
            self.log("success", "DA1 sync OK")
        elif sync:
            self.log("info", f"DA1 sent: 0x{sync[0]:02X}")
        else:
            self.log("warning", "No DA1 sync byte")

        # SYNC_SIGNAL - no ack expected
        self.log("info", "SYNC...")
        self.xsend(0x434E5953)

        # SETUP_ENVIRONMENT - send cmd, send params, read status
        self.log("info", "ENV...")
        self.xsend(0x010100)
        self.xsend_data(struct.pack("<IIIII", 0, 1, 1, 0, 0))
        st = self.xstatus(2000)
        self.log("info", f"  ENV→{st}")

        # SETUP_HW_INIT
        self.log("info", "HW...")
        self.xsend(0x010101)
        self.xsend_data(struct.pack("<I", 0))
        st = self.xstatus(2000)
        self.log("info", f"  HW→{st}")

        # DA1 SYNC response
        r = self.xread(5000)
        if r and len(r) >= 12:
            payload_hex = ' '.join(f'{b:02X}' for b in r[12:16]) if len(r) >= 16 else ''
            self.log("success", f"DA1 SYNC: {payload_hex}")
        else:
            self.log("warning", "DA1 SYNC: no response")

        # EMI AFTER full DA1 init (mtkclient sends EMI here, not before HW!)
        # SKIP in Preloader mode: the preloader already brought DRAM up, and
        # re-initialising it there fails (DRAM: None) and then BOOT_TO DA2 gets
        # NO RESPONSE. DA2 boots fine into the already-live DRAM.
        if getattr(self, "is_preloader", False):
            self.log("info", "Preloader mode: DRAM already up — skipping EMI init")
            return
        emi_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "emi_data.bin")
        if os.path.exists(emi_path):
            with open(emi_path, "rb") as f:
                emi = f.read()
            self.log("info", f"EMI ({len(emi)}b) after DA1 SYNC...")
            self.xsend(0x01000A)  # INIT_EXT_RAM
            st = self.xstatus(2000)
            self.log("info", f"  INIT: {st}")
            if st == 0:
                time.sleep(0.01)
                self.xsend(len(emi))
                self.xsend_data(emi)
                st = self.xstatus(10000)
                self.log("info", f"  DRAM: {st}")

    def _patch_da(self, da1, da2_full, da2_stripped, da2_sig):
        """Full DA1 patching from mtkclient: security bypasses + hash fix."""
        import hashlib
        da1 = bytearray(da1)
        da2 = bytearray(da2_full)
        patched = 0

        # === 1. patch_preloader_security_da1 (11 patterns from mtkclient) ===
        hex_patches = [
            ("A3687BB12846", "0123A3602846", "oppo_sec"),
            ("B3F5807F01D1", "B3F5807F01D14FF000004FF000007047", "mt6739"),
            ("B3F5807F04BF4FF4807305F011B84FF0FF307047",
             "B3F5807F04BF4FF480734FF000004FF000007047", "regular"),
            ("10B50C680268", "10B5012010BD", "ram_blacklist"),
            ("08B5104B7B441B681B68", "00207047000000000000", "usbdl_check"),
            ("F0B58BB002AE20250C460746", "002070470000000000205374617274", "sec_img_auth"),
            ("FFC0F3400008BD", "FF4FF0000008BD", "get_vfy_policy"),
            ("040007C0", "00000000", "hash_check"),
            ("CCF20709", "4FF00009", "hash_check2"),
        ]

        for pat_hex, rep_hex, name in hex_patches:
            pattern = bytes.fromhex(pat_hex)
            idx = da1.find(pattern)
            if idx != -1:
                replacement = bytes.fromhex(rep_hex)
                da1[idx:idx + len(replacement)] = replacement
                self.log("info", f"DA1: patched {name} @ {idx:#x}")
                patched += 1

        # Binary pattern patches
        bin_patches = [
            (b"\x14\x2C\xF6", "hash_check3"),  # search 3 bytes, patch 6 zeros
        ]
        for pat, name in bin_patches:
            idx = da1.find(pat)
            if idx != -1:
                da1[idx:idx + 6] = b"\x00\x00\x00\x00\x00\x00"
                self.log("info", f"DA1: patched {name} @ {idx:#x}")
                patched += 1

        # === 2. patch_da1 (version check bypass) ===
        vc = da1.find(b"\x1F\xB5\x00\x23\x01\xA8\x00\x93\x00\xF0")
        if vc != -1:
            da1[vc:vc + 4] = b"\x00\x20\x70\x47"
            self.log("info", f"DA1: patched version_check @ {vc:#x}")
            patched += 1

        # === 3. Patch DA2: bypass "Write data not allowed" (0xc002000c) +
        #     security checks, so the DA writes RO-flagged firmware partitions
        #     (lk/tee/preloader/...) like SP Flash Tool. Safe here: DA1's hash
        #     check is bypassed above, so the modified DA2 is accepted with no
        #     rehash. Each patch is pattern-matched and skipped if absent.
        da2_body = bytearray(da2_full[:-da2_sig] if da2_sig > 0 else da2_full)
        da2_patched = self._patch_da2_security(da2_body)

        self.log("info", f"DA1/DA2: {patched} DA1 patches applied")

        # Store patched DA2 for upload
        self._da2_patched = da2_patched
        return bytes(da1)

    def _patch_da2_security(self, da2):
        """Patch DA2 to write RO-protected firmware partitions and skip security
        checks — the exact patches mtkclient/SP Flash Tool rely on. Without the
        'write not allowed' patch the DA returns 0xc002000c for lk/tee/preloader/
        etc. Patterns are matched and skipped if absent (safe across DA builds);
        DA1's hash check is already bypassed so no rehash is needed."""
        da2 = bytearray(da2)
        applied = []

        def patch(pattern, repl, off=0, name=""):
            i = da2.find(pattern)
            if i != -1:
                da2[i + off:i + off + len(repl)] = repl
                applied.append(name)
                return True
            return False

        # --- "Write data not allowed" (0xc002000c) — THE fix for RO partitions
        if not patch(b"\x37\xB5\x00\x23\x04\x46\x02\xA8",
                     b"\x37\xB5\x00\x20\x03\xB0\x30\xBD", name="write_not_allowed"):
            if patch(b"\x0C\x23\xCC\xF2\x02\x03",
                     b"\x00\x23\x00\x23\x00\x23", name="write_not_allowed"):
                patch(b"\x2A\x23\xCC\xF2\x02\x03", b"\x00\x23\x00\x23\x00\x23",
                      name="write_not_allowed2")
        # --- security check ---
        patch(b"\x01\x23\x03\x60\x00\x20\x70\x47\x70\xB5", b"\x00\x23",
              name="security_check")
        # --- register read/write not allowed (0xC004000D) ---
        patch((0xC004000D).to_bytes(4, "little"), (0).to_bytes(4, "little"),
              name="reg_rw")
        # --- DA anti-rollback (0xC0020053) ---
        patch((0xC0020053).to_bytes(4, "little"), (0).to_bytes(4, "little"),
              name="anti_rollback")
        # --- hash check in cmd_boot_to (0xC0070004) ---
        patch((0xC0070004).to_bytes(4, "little"), (0).to_bytes(4, "little"),
              name="boot_hash")
        # --- SBC disable ---
        patch(b"\x02\x4B\x18\x68\xC0\xF3\x40\x00\x70\x47",
              b"\x4F\xF0\x00\x00", off=4, name="sbc")

        self.log("info", f"DA2 patches: {', '.join(applied) if applied else 'none found'}")
        if "write_not_allowed" not in applied:
            self.log("warning", "DA2 'write not allowed' pattern not found — "
                     "RO firmware partitions may still be rejected (0xc002000c)")
        return bytes(da2)

    def send_data(self, data):
        """Send data with XFlash header, chunked by USB packet size (512)."""
        header = struct.pack("<III", XFLASH_MAGIC, 1, len(data))
        self._write(header)
        # Send in 512-byte USB packets (matching EP maxPacketSize)
        pos = 0
        while pos < len(data):
            end = min(pos + 512, len(data))
            self._write(data[pos:end])
            pos = end

    def upload_da2(self, addr, data):
        """Upload DA2 - matches mtkclient boot_to() exactly."""
        self.log("info", f"BOOT_TO DA2: {len(data)} bytes → {addr:#x}")

        # 1. Send BOOT_TO command
        self.xsend(0x010008)

        # 2. Read status - dump raw for debugging
        r = self.xread(5000)
        if r:
            h = ' '.join(f'{b:02X}' for b in r[:20])
            self.log("info", f"  ready raw: [{h}] ({len(r)}b)")
        else:
            self.log("warning", "  ready: NO RESPONSE")

        # 3. Send param (addr + length)
        param = struct.pack("<QQ", addr, len(data))
        param_hdr = struct.pack("<III", XFLASH_MAGIC, 1, len(param))
        self._write(param_hdr)
        self._write(param)

        # 4. send_data(da2) - XFlash header + chunked data
        self.send_data(data)
        self.log("info", f"  DA2 sent ({len(data)} bytes)")

        # 5. Status after send (mtkclient checks this)
        st = self.xstatus(5000)
        self.log("info", f"  send: {st}")
        if st is not None and st != 0:
            self.log("warning", f"  DA2 send error: {st:#x}")

        # 6. DA2 boot
        self.log("info", "  Waiting for DA2...")
        time.sleep(2)  # Give DA2 time to init DRAM and boot

        # Try XFlash read
        r = self.xread(5000)
        if r:
            h = ' '.join(f'{b:02X}' for b in r[:20])
            self.log("success", f"  DA2: [{h}]")
        else:
            self.log("warning", "  DA2: no response (continuing)")
            # Try raw reads
            for _ in range(3):
                raw = self._read(512, timeout=1000)
                if raw:
                    self.log("info", f"  raw: [{' '.join(f'{b:02X}' for b in raw[:16])}]")
                    break

        self.log("info", "DA2 ready")

    def _da2_init(self):
        """Initialize DA2 after boot - required before write/format."""
        self.log("info", "DA2 init...")

        # SET_CHECKSUM_LEVEL = 0 (PLAIN)
        self.xsend(0x020003)  # SET_CHECKSUM_LEVEL
        r = self.xread(1000)
        if r:
            self.log("info", f"  CHKSUM → [{' '.join(f'{b:02X}' for b in r[:16])}]")
        self.xsend_data(struct.pack("<I", 0))  # level = 0
        r = self.xread(1000)
        if r:
            self.log("info", f"  level → [{' '.join(f'{b:02X}' for b in r[:16])}]")

        # SET_RESET_KEY = 0x68
        self.xsend(0x020004)  # SET_RESET_KEY
        r = self.xread(1000)
        if r:
            self.log("info", f"  RSTKEY → [{' '.join(f'{b:02X}' for b in r[:16])}]")
        self.xsend_data(struct.pack("<I", 0x68))
        r = self.xread(1000)
        if r:
            self.log("info", f"  key → [{' '.join(f'{b:02X}' for b in r[:16])}]")

        self.log("success", "DA2 init complete")

    # ==========================================================
    # Full init sequence
    # ==========================================================

    def full_init(self, da_path, regions):
        """DA1 → XFlash → DA2"""
        da1_addr, da1_data, da1_sig = regions[1]

        da2_addr, da2_data_full, da2_sig = regions[2] if len(regions) >= 3 else (0, b"", 0)
        da2_stripped = da2_data_full[:-da2_sig] if da2_sig > 0 else da2_data_full
        self.log("info", f"DA2: full={len(da2_data_full)}, stripped={len(da2_stripped)}, sig={da2_sig}")

        # Patch DA1: disable version check + fix hash for stripped DA2
        da1_data = self._patch_da(da1_data, da2_data_full, da2_stripped, da2_sig)

        # Upload patched DA1
        self.send_da(da1_addr, da1_data, da1_sig)
        self.jump_da(da1_addr)

        # XFlash sync
        self.da1_sync()

        # DA2 (patched + stripped)
        da2_to_send = getattr(self, '_da2_patched', da2_stripped)
        if da2_to_send:
            self.upload_da2(da2_addr, da2_to_send)

        # Ask DA2 for its required packet sizes and adopt the write size, so we
        # never desync the data stream (the cause of the silent-brick bug).
        self.get_packet_length()

        self.log("success", "DA fully initialized")

    def get_packet_length(self):
        """Query DA write/read packet sizes via DEVICE_CTRL + GET_PACKET_LENGTH.
        Adopts the DA's write_packet_length so write chunks match exactly."""
        DEVICE_CTRL = 0x010009
        GET_PACKET_LENGTH = 0x040007
        try:
            self.xsend(DEVICE_CTRL)
            self.xstatus(2000)
            self.xsend(GET_PACKET_LENGTH)
            self.xstatus(2000)
            pkt = self.xread(2000)
            payload = pkt[12:] if (len(pkt) >= 12 and
                struct.unpack_from("<I", pkt, 0)[0] == XFLASH_MAGIC) else pkt
            if len(payload) >= 8:
                wlen, rlen = struct.unpack_from("<II", payload, 0)
                if wlen:
                    self.write_packet_size = wlen
                self.log("info", f"DA packet length: write={wlen:#x} read={rlen:#x}")
                return wlen, rlen
        except Exception as e:
            self.log("warning", f"get_packet_length failed ({e}); "
                     f"using {self.write_packet_size:#x}")
        return None, None

    # ==========================================================
    # Flash operations
    # ==========================================================

    def _make_write_params(self, addr, size, storage=1, parttype=8):
        """Build write/read params matching mtkclient's format.
        storage: 1=EMMC, parttype: 8=USER"""
        # pack("<IIQQ", storage, parttype, addr, size) + NandExtension(8 zeros)
        param = struct.pack("<IIQQ", storage, parttype, addr, size)
        param += struct.pack("<IIIIIIII", 0, 0, 0, 0, 0, 0, 0, 0)  # NandExtension
        return param

    def write_partition(self, addr, data, progress_cb=None, storage=1, parttype=8):
        """Write data via XFlash WRITE_DATA.
        storage: 1=EMMC, parttype: 1=BOOT1, 2=BOOT2, 8=USER"""
        # Auto-wrap a raw preloader with the EMMC_BOOT/BRLYT boot-region header
        # when writing to a boot partition (BOOT1/BOOT2). The MTK BootROM looks
        # for the 'EMMC_BOOT' signature at the start of the boot region; a raw
        # GFH preloader ('MMM') written without it can't be found -> black
        # screen / falls back to BROM. SP Flash Tool builds this header
        # automatically. Generic for ALL MTK devices: only triggers on a raw
        # 'MMM' GFH that lacks the EMMC_BOOT wrapper, written to a boot region.
        if parttype in (1, 2) and data[:9] != b"EMMC_BOOT" and data[:4] == b"MMM\x01":
            try:
                from wrap_preloader import wrap as _wrap_pl
                wrapped = _wrap_pl(bytes(data))
                self.log("info", f"  preloader: added EMMC_BOOT/BRLYT header "
                         f"({len(data)} → {len(wrapped)} bytes)")
                data = wrapped
            except Exception as e:
                self.log("warning", f"  preloader auto-wrap failed: {e}")

        # Pad data to 512-byte alignment (eMMC sector size)
        if len(data) % 512 != 0:
            pad = 512 - (len(data) % 512)
            data = data + b'\x00' * pad
            self.log("info", f"  padded {pad} bytes → {len(data)}")

        self.log("info", f"WRITE: {len(data)} bytes @ {addr:#x} (part={parttype})")

        # Forensic: open a write record with head/tail hexdump so a wrong or
        # missing image header (e.g. preloader SV5_BL) or byte-shift is visible.
        _rec = None
        if self.flog:
            _rec = self.flog.begin_write(
                name=f"part{parttype}@{addr:#x}", addr=addr, size=len(data),
                parttype=parttype, storage=storage,
                head=data[:64], tail=data[-64:],
            )

        # Flush any leftover from previous operations
        while True:
            r = self.xread(200)
            if not r:
                break

        # 1. Send WRITE_DATA command
        self.log("info", "  sending cmd...")
        self.xsend(0x010004)

        # 2. Read status
        st = self.xstatus(2000)
        self.log("info", f"  cmd: {st}{f' ({st:#x})' if st else ''}")

        # 3. Send params
        self.log("info", "  sending params...")
        param = self._make_write_params(addr, len(data), storage, parttype)
        if self.flog:
            self.flog.log_write_params(param, addr, len(data), storage, parttype)
        try:
            self.xsend_data(param)
            self.log("info", "  params sent OK")
        except Exception as e:
            self.log("error", f"  params WRITE FAILED: {e}")
            return False

        # 4. Read write-ready status
        st = self.xstatus(5000)
        self.log("info", f"  ready: {st}{f' ({st:#x})' if st else ''}")

        if st is not None and (st & 0xC0000000) == 0xC0000000:
            self.log("warning", f"  write rejected: {st:#x}")
            return False

        # 5. Send data: [flags(4) + checksum(4) + data] per chunk
        # Each sent as send_param([flags, checksum, data]) = 3 XFlash packets
        time.sleep(0.1)
        pkt = self.write_packet_size  # DA-negotiated size (must match exactly)
        sent = 0
        self.log("info", f"  writing {len(data)} bytes ({pkt}b chunks)...")

        # Cache endpoint write for minimal Python overhead
        _ep_write = self.ep_out.write

        def _send_param_list(parts):
            """Optimized: minimal Python overhead, fewer writes."""
            for part in parts:
                hdr = struct.pack("<III", XFLASH_MAGIC, 1, len(part))
                _ep_write(hdr, timeout=30000)
                # Single write per part - pyusb handles USB packet split internally
                # Much faster than manual chunking on macOS
                _ep_write(part, timeout=30000)
            return self.xstatus(30000)

        last_progress_update = 0
        progress_interval = max(0x400000, pkt * 2)  # update every 4MB min

        # Speed tracking
        import time as _time
        t_start = _time.time()
        last_log_bytes = 0
        log_interval = 100 * 1024 * 1024  # log every 100MB

        # Pipeline: prepare next chunk's checksum+buffers while USB sends current
        import threading
        import queue

        FLAGS_PKT = struct.pack("<I", 0x0)
        total_len = len(data)
        prep_queue = queue.Queue(maxsize=4)  # buffered chunks ready to send

        # Forensic accumulators: full-image SHA-256 + running checksum, computed
        # in the prep thread so it costs nothing extra (no second pass over data).
        import hashlib as _hashlib
        _hasher = _hashlib.sha256()
        _acc = {"cksum": 0}

        def _prep_worker():
            """Background: slice chunks + calculate checksums ahead of USB."""
            pos = 0
            while pos < total_len:
                end_pos = min(pos + pkt, total_len)
                chunk = data[pos:end_pos]
                csum = sum(chunk) & 0xFFFF
                csum_pkt = struct.pack("<I", csum)
                _hasher.update(chunk)
                _acc["cksum"] = (_acc["cksum"] + csum) & 0xFFFFFFFF
                prep_queue.put((chunk, csum_pkt, end_pos))
                pos = end_pos
            prep_queue.put(None)  # sentinel

        prep_thread = threading.Thread(target=_prep_worker, daemon=True)
        prep_thread.start()

        while True:
            item = prep_queue.get()
            if item is None:
                break
            chunk, csum_pkt, end = item

            # Send: [flags=0x0] [checksum] [data]
            st = _send_param_list([FLAGS_PKT, csum_pkt, chunk])

            if st is not None and (st & 0xC0000000) == 0xC0000000:
                self.log("error", f"  error @{sent:#x}: {st:#x}")
                return False

            sent = end
            if progress_cb and (sent - last_progress_update >= progress_interval or sent == total_len):
                progress_cb(sent, total_len)
                last_progress_update = sent

            # Speed log every 100MB (cheap - only log() call, no USB work)
            if sent - last_log_bytes >= log_interval:
                elapsed = _time.time() - t_start
                speed_mb = (sent / elapsed / 1024 / 1024) if elapsed > 0 else 0
                pct = sent * 100 // total_len if total_len else 0
                self.log("info", f"  {sent // (1024*1024)} MB / "
                         f"{total_len // (1024*1024)} MB ({pct}%) @ {speed_mb:.1f} MB/s")
                last_log_bytes = sent

        prep_thread.join(timeout=1)

        self.log("success", f"  write complete ({sent} bytes)")
        st = self.xstatus(30000)
        self.log("info", f"  final: {st}")
        if self.flog and _rec is not None:
            self.flog.end_write(
                _rec, ok=True,
                sha256_written=_hasher.hexdigest(),
                checksum_written=_acc["cksum"],
                da_status=st,
            )
        return True

    def read_partition(self, addr, size, progress_cb=None, storage=1, parttype=8):
        """Read `size` bytes from flash via XFlash READ_DATA (0x010005).

        Mirrors write_partition's param structure. Returns the raw bytes.
        NOTE: read framing varies slightly between DA builds — validate the
        first readback against a known image before trusting verification.
        """
        self.log("info", f"READ: {size} bytes @ {addr:#x} (part={parttype})")
        while self.xread(200):
            pass

        self.xsend(0x010005)  # READ_DATA
        st = self.xstatus(2000)
        self.log("info", f"  cmd: {st}{f' ({st:#x})' if st else ''}")

        param = self._make_write_params(addr, size, storage, parttype)
        self.xsend_data(param)
        # mtkclient reads TWO statuses here: one acking the params (send_param's
        # internal status) and one "ready to stream" status. Missing the second
        # makes the first data header look like an end-flag → 0 bytes read.
        st = self.xstatus(5000)
        st = self.xstatus(5000)
        self.log("info", f"  ready: {st}{f' ({st:#x})' if st else ''}")
        if st is not None and (st & 0xC0000000) == 0xC0000000:
            self.log("error", f"  read rejected: {st:#x}")
            return b""

        # Mirror mtkclient exactly: read a precise 12-byte header, then the
        # declared payload, then ACK each data packet with a status packet.
        ack = struct.pack("<IIII", XFLASH_MAGIC, 1, 4, 0)
        out = bytearray()
        while len(out) < size:
            hdr = self._read_exact(12, timeout=5000)
            magic, _, slength = struct.unpack("<III", hdr)
            if magic != XFLASH_MAGIC:
                self.log("error", f"  read: bad magic {magic:#x}")
                break
            payload = self._read_exact(slength, timeout=30000)
            if slength > 4:
                out.extend(payload)
                self._write(ack)  # per-packet acknowledge
                if progress_cb:
                    progress_cb(min(len(out), size), size)
            elif slength == 4:
                # status/flag packet — non-zero means error
                if struct.unpack("<I", payload)[0] != 0:
                    self.log("error", "  read completed with error status")
                break
        # Final acknowledge packet from DA
        try:
            fhdr = self._read_exact(12, timeout=5000)
            _, _, flen = struct.unpack("<III", fhdr)
            if flen:
                self._read_exact(flen, timeout=5000)
        except Exception:
            pass
        return bytes(out[:size])

    def verify_partition(self, addr, size, expected_sha256, record=None,
                         progress_cb=None, storage=1, parttype=8):
        """Read the region back and compare SHA-256 to what we wrote.

        This is the definitive silent-brick detector: a write can return OK at
        the protocol level yet land corrupted/shifted bytes on storage. Returns
        True on match. Opt-in (never called automatically) so it can't slow or
        destabilise a normal flash until validated.
        """
        self.log("info", f"VERIFY: reading back {size} bytes @ {addr:#x}...")
        data = self.read_partition(addr, size, progress_cb, storage, parttype)
        if len(data) != size:
            self.log("error", f"  verify read short: {len(data)}/{size}")
            return False
        import hashlib as _h
        got = _h.sha256(data).hexdigest()
        if self.flog and record is not None:
            self.flog.record_readback(record, got)
        match = (got == expected_sha256)
        self.log("success" if match else "error",
                 f"  verify {'MATCH' if match else 'MISMATCH'}: {got}")
        return match

    def reset(self, bootmode=0):
        """Reset the device out of DA mode so it reboots into the flashed
        firmware. Mirrors mtkclient's xflash shutdown().
        bootmode: 0=normal, 1=home screen (boot OS), 2=fastboot."""
        SHUTDOWN = 0x010007
        try:
            self.xsend(SHUTDOWN)
            st = self.xstatus(2000)
            self.log("info", f"RESET: shutdown cmd status={st}")
            hasflags = 1 if bootmode != 0 else 0
            # hasflags, enablewdt, async, bootmode, dl_bit, dont_resetrtc, leaveusb, rsv
            param = struct.pack("<IIIIIIII", hasflags, 0, 0, bootmode, 0, 0, 0, 0)
            self.xsend_data(param)
            st = self.xstatus(2000)
            self.log("success" if st == 0 else "warning",
                     f"RESET: final status={st} — device should reboot")
            return st == 0
        except Exception as e:
            self.log("warning", f"reset failed: {e}")
            return False

    def flush_log_report(self):
        """Emit the end-of-session forensic report (call after a flash run)."""
        if self.flog:
            self.flog.report()

    def _da_status(self, timeout=120000):
        """Faithful port of mtkclient xflash status(): read EXACTLY a 12-byte
        header + exactly `length` payload bytes (no 512-chunk over-read), and
        decode 2-byte and 4-byte statuses the same way. This exactness is what
        the FORMAT handshake needs — the old xstatus() returned None for 2-byte
        statuses and over-read packets, which desynced FORMAT."""
        hdr = self._read_exact(12, timeout=timeout)
        magic, _dt, length = struct.unpack("<III", hdr)
        if magic != XFLASH_MAGIC:
            return -1
        tmp = self._read_exact(length, timeout=timeout)
        if length == 2:
            return struct.unpack("<H", tmp)[0]
        elif length == 4:
            v = struct.unpack("<I", tmp)[0]
            return 0 if v == XFLASH_MAGIC else v
        return struct.unpack("<I", tmp[:4])[0]

    def format_partition(self, addr, length, storage=1, parttype=8):
        """Format (erase) a region via XFlash FORMAT (0x010003).

        Direct port of mtkclient's formatflash():
            xsend(FORMAT); status()            # cmd ack
            send_param(param); status()        # param ack, then first status
            while status == CONTINUE(0x40040004):
                sleep(status() ms); status = ack()
            ok = status == COMPLETE(0x40040005)
        Uses _da_status() (exact reads) so each format fully drains through
        COMPLETE and the next operation starts clean (no desync)."""
        STATUS_CONTINUE = 0x40040004
        STATUS_COMPLETE = 0x40040005
        self.log("info", f"FORMAT: {length:#x} @ {addr:#x} (part={parttype})")

        try:
            self.xsend(0x010003)              # FORMAT
            st = self._da_status(10000)       # cmd ack
            if st != 0:
                self.log("warning", f"  format cmd status: {st:#x}")
                return False

            param = self._make_write_params(addr, length, storage, parttype)
            self.xsend_data(param)
            st = self._da_status(20000)       # send_param status (param ack)
            if st != 0:
                self.log("warning", f"  format param status: {st:#x}")
                return False

            # Robust drain: this DA emits an extra 0x0 status before the
            # CONTINUE stream, so read until COMPLETE/error. Treat 0 as an
            # intermediate "keep waiting" tick; ack every CONTINUE. Bounded by
            # an overall deadline so a desync can't hang forever.
            ok = False
            deadline = time.time() + 600
            while time.time() < deadline:
                st = self._da_status(120000)
                if st == STATUS_COMPLETE:
                    ok = True
                    break
                if st == STATUS_CONTINUE:
                    sleep_val = self._da_status(120000)    # ms to wait
                    if isinstance(sleep_val, int) and 0 < sleep_val < 10000:
                        time.sleep(sleep_val / 1000.0)
                    self.xsend(0x0)                        # ack
                    continue
                if st == 0:
                    continue                               # intermediate tick
                if st is None or st < 0 or (st & 0xC0000000) == 0xC0000000:
                    self.log("warning", f"  format error status: "
                             f"{'None' if st is None else f'{st:#x}'}")
                    break
                # unknown non-terminal value -> keep reading
            self.log("success" if ok else "warning",
                     f"  format {'done' if ok else 'incomplete'}")
            return ok
        except Exception as e:
            self.log("warning", f"  format error: {e}")
            return False
