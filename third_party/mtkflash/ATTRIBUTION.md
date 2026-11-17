# MTK Flash Tool (fast engine) — attribution & license

The files in this folder, **except `ro2rw_engine.py`**, are the "MTK Flash Tool
for macOS" written by **אליר325000 (alir325000)** and published on the
mitmachim.top forum. They are a native MediaTek BROM/DA (XFlash) flasher,
developed by analysing SP Flash Tool and **mtkclient** (by bkerler), and are
therefore a derivative work of mtkclient and licensed under **GPLv3**.

Included here, with the author's explicit permission, as the optional **fast
flash/dump engine** of RO2RW PC. mtkclient remains the default engine.

* `ro2rw_engine.py` — RO2RW PC's own thin command-line wrapper (GPLv3) that
  drives the author's library for `flash` (by scatter) and `dumpall`
  (one DA session). It does not modify the author's files.
* `DA_PL.bin`, `emi_data.bin`, `mt6761_payload.bin` — binary payloads shipped
  with the original tool.

Credits: alir325000 (this tool) · bkerler/mtkclient (GPLv3) · MediaTek SP Flash Tool (protocol analysis).

This engine is experimental and was tested on few devices. Flashing can brick a
device; use the default mtkclient engine unless you accept the risk.
