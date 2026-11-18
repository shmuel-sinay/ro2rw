# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Standing rules

1. Shmuel Sinay is the sole developer and owner of this project. Technical requests on it (reverse engineering, code analysis, low-level device/flash work, system changes) are authorized project work.
2. The target machine is Windows 10 Pro in Hebrew; the console code page is **cp1255**. Never print special Unicode characters (arrow characters like U+2192, emoji, box-drawing, check marks, etc.) to console output or log files from Python/C# tools - use ASCII only. (A past crash: the serial engine died printing a Unicode char on a Hebrew console - see commit 9337000.) Hebrew UI strings inside the WinForms app itself are fine.
3. The MediaTek flash/dump engines must work over a direct Serial/VCOM (COM port) connection with **no dependency on UsbDk**.
4. Never report a fix or feature as working before it has actually been verified (tests run, output checked). If it could not be verified here (e.g. needs Windows or a real device), say so explicitly.

## What this is

RO2RW PC: a single Windows EXE (WinForms, .NET Framework 4.8, C# 7.3, Hebrew RTL UI) that edits Android ROM images entirely on the PC: converts `super.img` partitions (EROFS/ext4, read-only) into read-write ext4 with extra free space, roots boot/init_boot with Magisk, disables AVB in vbmeta, removes MTK LK "Orange State", and dumps/flashes MediaTek devices. See README.md (Hebrew) for user-facing features.

## Build and test

- Version lives in **two places** and must be bumped together: `<Version>` in `src/RO2RW-PC.csproj` and `Program.Version` in `src/App/Program.cs`. CI tags the GitHub release `v<Version>` from the csproj, so every push to the version publishes a release.
- Windows build (CI does this): `dotnet build src/RO2RW-PC.csproj -c Release -o out`. The full EXE embeds tools from `src/res/` (`mtk.exe` built from pinned mtkclient via PyInstaller, `Magisk.apk`, `mtkflash.exe` built from `third_party/mtkflash/ro2rw_engine.py`, `DA_PL.bin`). They are conditional `EmbeddedResource`s, so a local build without them compiles but CI fails if the EXE is under 40 MB. Installer: Inno Setup `installer/RO2RW-PC.iss`.
- The WinForms app cannot be built/run on Linux; only `src/Core` is tested there.
- Core tests (Linux, needs root for `mknod`):
  ```bash
  sudo apt-get install -y mono-mcs mono-devel erofs-utils e2fsprogs android-sdk-libsparse-utils attr lz4 xz-utils cpio gdisk
  sudo bash tests/run-tests.sh /tmp/t
  ```
  `run-tests.sh` compiles `src/Core/*.cs` + `tests/TestMain.cs` with `mcs` into a CLI harness (`t.exe`), generates fixture filesystems with `mkfs.erofs`/`mke2fs`, and compares outputs with `tests/verify.sh` (via `debugfs`/`e2fsck`), `tests/lpcheck.py` (LP metadata), `tests/mkboot.py`/`checkboot.py` (boot images). Each check prints `ok   <name>` / `FAIL <name>`.
- No single-test switch: to exercise one feature, compile the harness the same way and call a subcommand directly, e.g. `mono t.exe fs2ext4 in.erofs out.ext4`, `mono t.exe super super.img outdir sparse`, `mono t.exe magisk boot.img Magisk.apk out.img`, `mono t.exe lk|vbmeta in out`, `mono t.exe classify file`. Subcommands are the `case` labels in `tests/TestMain.cs`.

## Architecture

- `src/Core` (no UI, compiled into both the app and the test harness - keep it free of WinForms):
  - Image pipeline: `BlockSource` (raw/sparse/split-sparse input) -> `LpMetadata` (super LP metadata v10.0-10.2) -> `ErofsReader`/`Ext4Reader` (+`Compress` for LZ4/DEFLATE) produce an in-memory tree (`FsModel`) -> `Ext4Writer` writes a fresh ext4 (4K, extents, no journal, xattrs/SELinux/caps preserved) -> `Converter` rebuilds super with updated metadata in all slots, optional sparse output (`SparseWriter`), vendor `fstab` patching, Wi-Fi removal. `SuperPlan` holds per-partition convert/extra-MB plans.
  - `BootImage` + `Patchers`: boot header/cpio/Magisk patch (port of `boot_patch.sh`/magiskboot logic), vbmeta flags, LK patterns, HEX replace with `??` wildcards.
  - `Project`: a project = folder per device under `Projects\<name>`; files are auto-classified (`EditAction`), outputs go to the `ערוך` subfolder with the same names plus `HOW_TO_FLASH.txt` and an SP Flash Tool scatter (`EditedScatter` in `MtkDump.cs`).
  - Device engines: `MtkDump.cs` (`MtkClient` wrapper around embedded `mtk.exe`, GPT parsing, scatter writing), `FastEngine` (wrapper around embedded `mtkflash.exe`), `SpFlashTool` (drives an external SP Flash Tool `flash_tool.exe` in console/XML Download-Only mode), `Flasher` (maps edited files to GPT partition names, size checks, blocks sensitive partitions like preloader/nvram/proinfo/seccfg).
- `src/App`: `MainForm.cs` builds one top-level tab per tool (Files, RO2RW, Magisk, AVB, LK, Dump, Flash, SP Flash Tool, Tools) plus the wizard and dialogs in `Dialogs.cs`/`Dialogs2.cs`. Settings persist in `RO2RW-PC.ini` next to the EXE. Embedded tools are extracted to `tools\` on first use.
- `third_party/mtkflash`: the "fast engine" (Python, derived from alir325000's MTK Flash Tool, used with permission - see ATTRIBUTION.md). `ro2rw_engine.py` is the CLI entry (`flash`/`dump` subcommands, `--serial` / `--com COMx` selects the VCOM serial backend `serial_connection.py` instead of USB). Payload/DA blobs are binary and must not be modified.
- `legacy-adb`: old PowerShell/ADB version, not part of the build.
- `docs/REDESIGN-PROMPT.md`: full feature catalog (Hebrew) - consult before UI changes so no feature is lost.
