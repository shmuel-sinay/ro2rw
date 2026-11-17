"""
Forensic flash logger for MTK Flash Tool.

Purpose: capture *everything* about a flash session so that a write which
"reports success" but leaves the device bricked can still be diagnosed
afterwards from the log file alone.

Design goals:
  - Drop-in replacement for the existing `log(level, msg)` callback used by
    MTKDevice: a FlashLogger instance is itself callable as log(level, msg),
    so it tees every existing log line to both the GUI and a timestamped file.
  - Rich helpers for the things that actually explain a silent brick:
      * exact bytes of the WRITE_DATA params (hex)
      * SHA-256 + simple checksum of every image written
      * head/tail hexdump of every image (catches wrong/missing preloader
        headers, byte-shift, endian, truncation)
      * a per-partition session report at the end with WRITTEN vs READBACK
        hashes when verification is enabled.
"""

import os
import sys
import time
import hashlib
import struct
from dataclasses import dataclass, field
from typing import Optional, List, Callable


def _ts() -> str:
    return time.strftime("%H:%M:%S")


def hexdump(data: bytes, length: int = 64, base_addr: int = 0) -> str:
    """Classic 16-byte-per-row hexdump of the first `length` bytes."""
    out = []
    view = data[:length]
    for off in range(0, len(view), 16):
        row = view[off:off + 16]
        hexs = " ".join(f"{b:02x}" for b in row)
        hexs = f"{hexs:<47}"
        ascii_ = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        out.append(f"    {base_addr + off:08x}  {hexs}  |{ascii_}|")
    return "\n".join(out)


@dataclass
class WriteRecord:
    name: str
    addr: int
    size: int
    parttype: int
    storage: int
    sha256_written: str = ""
    checksum_written: int = 0
    sha256_readback: str = ""
    da_status: Optional[int] = None
    ok: bool = False
    verified: Optional[bool] = None  # None = not verified
    t_start: float = field(default_factory=time.time)
    t_end: float = 0.0

    @property
    def seconds(self) -> float:
        return (self.t_end or time.time()) - self.t_start


class FlashLogger:
    """Callable logger that tees to a timestamped file + the GUI callback."""

    def __init__(self, gui_cb: Optional[Callable] = None, log_dir: str = None):
        if log_dir is None:
            log_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs")
        os.makedirs(log_dir, exist_ok=True)
        self.path = os.path.join(log_dir, f"flash_{time.strftime('%Y%m%d_%H%M%S')}.log")
        self._f = open(self.path, "a", buffering=1, encoding="utf-8")
        self._gui = gui_cb
        self.records: List[WriteRecord] = []
        self._banner()

    # -- core callable: drop-in for MTKDevice.log(level, msg) ----------------
    def __call__(self, level: str, msg: str):
        line = f"{_ts()} [{str(level).upper():<7}] {msg}"
        try:
            self._f.write(line + "\n")
        except Exception:
            pass
        if self._gui:
            try:
                self._gui(level, msg)
            except Exception:
                pass

    def raw(self, text: str = ""):
        """Write text straight to the file (no level), not to the GUI."""
        try:
            self._f.write(text + "\n")
        except Exception:
            pass

    def _banner(self):
        self.raw("=" * 78)
        self.raw(f"  MTK Flash Tool — forensic log")
        self.raw(f"  started: {time.strftime('%Y-%m-%d %H:%M:%S')}")
        self.raw(f"  python : {sys.version.split()[0]}  platform: {sys.platform}")
        self.raw("=" * 78)

    # -- session / device context -------------------------------------------
    def device_info(self, **kw):
        self.raw("")
        self.raw("--- device / DA context ---")
        for k, v in kw.items():
            if isinstance(v, int) and not isinstance(v, bool):
                self.raw(f"  {k:<18}: {v} ({v:#x})")
            else:
                self.raw(f"  {k:<18}: {v}")
        self.raw("")

    # -- write instrumentation ----------------------------------------------
    def log_write_params(self, raw_params: bytes, addr: int, size: int,
                         storage: int, parttype: int):
        self.raw(f"  WRITE_DATA params ({len(raw_params)} bytes):")
        self.raw(f"    storage={storage} parttype={parttype} "
                 f"addr={addr:#x} size={size:#x}")
        self.raw("    raw: " + raw_params.hex())

    def begin_write(self, name: str, addr: int, size: int,
                    parttype: int, storage: int,
                    head: bytes = b"", tail: bytes = b"") -> WriteRecord:
        rec = WriteRecord(name=name, addr=addr, size=size,
                          parttype=parttype, storage=storage)
        self.records.append(rec)
        self.raw("")
        self.raw(f"### WRITE '{name}'  addr={addr:#x} size={size} "
                 f"({size/1024/1024:.1f} MB) part={parttype} storage={storage}")
        if head:
            self.raw("  head (first 64 bytes):")
            self.raw(hexdump(head, 64, addr))
        if tail:
            self.raw("  tail (last 64 bytes):")
            self.raw(hexdump(tail, 64, addr + max(0, size - len(tail[:64]))))
        return rec

    def end_write(self, rec: WriteRecord, ok: bool,
                  sha256_written: str, checksum_written: int,
                  da_status: Optional[int]):
        rec.ok = ok
        rec.sha256_written = sha256_written
        rec.checksum_written = checksum_written
        rec.da_status = da_status
        rec.t_end = time.time()
        self.raw(f"  written sha256 : {sha256_written}")
        self.raw(f"  written cksum  : {checksum_written:#010x}")
        self.raw(f"  DA final status: "
                 f"{'None' if da_status is None else f'{da_status:#x}'}")
        self.raw(f"  result: {'OK' if ok else 'FAIL'}  "
                 f"({rec.seconds:.1f}s)")

    def record_readback(self, rec: WriteRecord, sha256_readback: str):
        rec.sha256_readback = sha256_readback
        rec.verified = (sha256_readback == rec.sha256_written)
        tag = "MATCH ✓" if rec.verified else "MISMATCH ✗  <-- DATA CORRUPTED ON DEVICE"
        self.raw(f"  readback sha256: {sha256_readback}  [{tag}]")
        if not rec.verified:
            self("error", f"VERIFY FAILED for '{rec.name}': "
                          f"device content != written content")

    # -- final report -------------------------------------------------------
    def report(self) -> str:
        lines = ["", "=" * 78, "  SESSION REPORT", "=" * 78,
                 f"  {'partition':<16}{'addr':>14}{'size':>14}  "
                 f"{'status':<8}{'verify':<10}", "  " + "-" * 70]
        any_unverified_brick_risk = False
        for r in self.records:
            if r.verified is None:
                verify = "—"
            elif r.verified:
                verify = "MATCH"
            else:
                verify = "MISMATCH!"
            status = "OK" if r.ok else "FAIL"
            if r.ok and r.verified is None:
                any_unverified_brick_risk = True
            lines.append(f"  {r.name:<16}{('%#x' % r.addr):>14}{r.size:>14}  "
                         f"{status:<8}{verify:<10}")
        lines.append("  " + "-" * 70)
        mism = [r.name for r in self.records if r.verified is False]
        if mism:
            lines.append(f"  ⚠ MISMATCH on: {', '.join(mism)} — "
                         f"protocol is corrupting data (explains silent brick).")
        elif any_unverified_brick_risk:
            lines.append("  ⚠ writes reported OK but were NOT verified by readback.")
            lines.append("    Enable verification to confirm bytes landed correctly.")
        else:
            lines.append("  ✓ all writes verified against device readback.")
        lines.append(f"  log file: {self.path}")
        lines.append("=" * 78)
        text = "\n".join(lines)
        self.raw(text)
        # also surface the summary through the GUI log
        for ln in text.splitlines():
            if self._gui:
                try:
                    self._gui("info", ln)
                except Exception:
                    pass
        return text

    def close(self):
        try:
            self._f.flush()
            self._f.close()
        except Exception:
            pass
