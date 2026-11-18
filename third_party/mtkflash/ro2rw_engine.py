#!/usr/bin/env python3
# RO2RW PC - thin command-line wrapper around alir325000's MTK Flash Tool library.
# This file is RO2RW PC's own code; it does not modify the bundled tool.
# License: GPLv3 (it links the GPLv3 MTK Flash Tool library).
#
#   ro2rw_engine.py flash   --scatter S.txt --da DA_PL.bin [--partitions a,b] [--no-reboot]
#   ro2rw_engine.py dumpall --da DA_PL.bin --out DIR [--skip a,b] [--gpt-bytes N] [--no-reboot]
#
# dumpall connects once, reads the GPT, then reads every partition in a single
# DA session (the bundled CLI re-handshakes per partition, which is far slower).
import argparse, os, struct, sys
# Force UTF-8 output so prints with non-ASCII chars (e.g. the -> arrow in the
# bundled tool's logs) never crash on a non-UTF-8 Windows console (cp1255 etc.).
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
# Windows: make pyusb locate a libusb-1.0 backend (shipped by libusb-package).
try:
    import usb.backend.libusb1 as _lb1
    import libusb_package as _lp
    _orig_get_backend = _lb1.get_backend
    def _get_backend(find_library=None):
        return _orig_get_backend(find_library=_lp.find_library)
    _lb1.get_backend = _get_backend
except Exception:
    pass


# Optional: talk to the device over the MediaTek VCOM serial COM port (pyserial)
# instead of libusb/UsbDk - exactly how SP Flash Tool connects. Lets the tool work
# on devices where UsbDk will not bind, without SP Flash Tool itself.
def _use_serial(com=None):
    import flasher as _fl, serial_connection as _sc

    class _SerialShim(_sc.SerialConnection):
        max_out_size = 512

        def __init__(self):
            super().__init__()
            self.max_out_size = 512
            if com:
                self._forced = com

        def find_device(self, timeout_sec=60.0):
            if com:
                self.port_name = com
                print("[SERIAL] Using forced port: " + com, flush=True)
                return True
            return super().find_device(timeout_sec)

        def set_line_coding(self, baudrate=921600):
            try:
                self.set_baudrate(baudrate)
            except Exception:
                pass

        def find_and_connect(self, timeout_sec=120.0):
            return super().find_and_connect(timeout_sec)

    _fl.USBConnection = _SerialShim
    print("[SERIAL] Using MTK VCOM serial backend (no UsbDk).", flush=True)
from flasher import MTKFlasher, FlashError
from usb_connection import USBError
from brom import BromError
from da_protocol import DAError


def _u(s):  # robust int parse
    return int(str(s), 0)


def parse_gpt(data):
    """Return (sector_size, [(name, start_byte, size_byte), ...]) from a GPT dump."""
    for ss in (512, 4096):
        if len(data) < ss + 92 or data[ss:ss + 8] != b"EFI PART":
            continue
        entries_lba = struct.unpack_from("<Q", data, ss + 72)[0]
        count = struct.unpack_from("<I", data, ss + 80)[0]
        esz = struct.unpack_from("<I", data, ss + 84)[0]
        base = entries_lba * ss
        parts = []
        for i in range(count):
            o = base + i * esz
            if o + 128 > len(data):
                break
            if data[o:o + 16] == b"\x00" * 16:
                continue
            first = struct.unpack_from("<Q", data, o + 32)[0]
            last = struct.unpack_from("<Q", data, o + 40)[0]
            name = data[o + 56:o + 128].decode("utf-16-le").split("\x00")[0]
            if name:
                parts.append((name, first * ss, (last - first + 1) * ss))
        if parts:
            return ss, parts
    raise FlashError("No GPT found in the dumped area (device may use a different layout)")


def cmd_flash(a):
    MTKFlasher().flash_firmware(
        scatter_path=a.scatter, da_path=a.da, da_addr=a.da_addr,
        partitions=[p.strip() for p in a.partitions.split(",")] if a.partitions else None,
        skip_format=True, reboot=not a.no_reboot)


def cmd_dumpall(a):
    os.makedirs(a.out, exist_ok=True)
    skip = set(s.strip().lower() for s in a.skip.split(",") if s.strip()) if a.skip else set()
    f = MTKFlasher()
    try:
        f.connect()
        f.upload_da(a.da, a.da_addr)
        print(f"[*] Reading GPT ({a.gpt_bytes:#x} bytes)...", flush=True)
        head = f.da.read_partition(0, a.gpt_bytes)
        with open(os.path.join(a.out, "gpt.bin"), "wb") as w:
            w.write(head)
        ss, parts = parse_gpt(head)
        print(f"[*] GPT ok: {len(parts)} partitions, sector {ss}", flush=True)
        done = 0
        for name, start, size in parts:
            if name.lower() in skip:
                print(f"[-] skip {name}", flush=True)
                continue
            out = os.path.join(a.out, name + ".img")
            print(f"[*] Dumping {name} ({size} bytes) -> {name}.img", flush=True)
            data = f.da.read_partition(start, size)
            with open(out, "wb") as w:
                w.write(data[:size])
            done += 1
        print(f"[*] Dumped {done} partitions", flush=True)
        f.finish(reboot=not a.no_reboot)
    except (FlashError, BromError, DAError, USBError) as e:
        print(f"\n[ERROR] {e}", flush=True)
        try:
            f.finish(reboot=False)
        except Exception:
            pass
        sys.exit(1)


def main():
    p = argparse.ArgumentParser(prog="ro2rw_engine")
    sub = p.add_subparsers(dest="cmd", required=True)
    pf = sub.add_parser("flash")
    pf.add_argument("--scatter", required=True)
    pf.add_argument("--da", required=True)
    pf.add_argument("--da-addr", type=_u, default=0x200000)
    pf.add_argument("--partitions")
    pf.add_argument("--no-reboot", action="store_true")
    pf.add_argument("--serial", action="store_true", help="use MTK VCOM serial (no UsbDk)")
    pf.add_argument("--com", help="force COM port, e.g. COM40")
    pf.set_defaults(fn=cmd_flash)
    pd = sub.add_parser("dumpall")
    pd.add_argument("--da", required=True)
    pd.add_argument("--out", required=True)
    pd.add_argument("--da-addr", type=_u, default=0x200000)
    pd.add_argument("--skip")
    pd.add_argument("--gpt-bytes", type=_u, default=0x400000)
    pd.add_argument("--no-reboot", action="store_true")
    pd.add_argument("--serial", action="store_true", help="use MTK VCOM serial (no UsbDk)")
    pd.add_argument("--com", help="force COM port, e.g. COM40")
    pd.set_defaults(fn=cmd_dumpall)
    a = p.parse_args()
    if getattr(a, "serial", False):
        _use_serial(getattr(a, "com", None))
    a.fn(a)


if __name__ == "__main__":
    main()
