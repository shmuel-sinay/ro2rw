#!/bin/bash
# Core tests (Linux): requires mono-mcs, erofs-utils, e2fsprogs, android-sdk-libsparse-utils, attr
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
W=${1:-$(mktemp -d)}
mkdir -p "$W"
mcs -nologo -r:System.IO.Compression -r:System.IO.Compression.FileSystem -out:"$W/t.exe" "$ROOT"/src/Core/*.cs "$ROOT/tests/TestMain.cs"
T="mono $W/t.exe"
V="$ROOT/tests/verify.sh"
cd "$W"; rm -rf src; mkdir -p src/etc src/bin src/app/deep/dir src/big src/many
echo hello > src/etc/a.txt; : > src/etc/empty
printf '/dev/block/dm-0 /vendor erofs ro wait,logical,first_stage_mount\n' > src/etc/fstab.qcom
head -c 3000000 /dev/urandom > src/big/rand.bin
python3 -c "
import random
random.seed(1)
w=['android','system','vendor','lib','so','apk','framework']
open('src/big/text.txt','w').write(' '.join(random.choice(w) for _ in range(900000)))
open('src/big/odd.bin','wb').write(bytes(random.randrange(256) if i%3 else 0 for i in range(777777)))
"
for i in $(seq 1 300); do echo "file $i $(seq 1 $i)" > src/app/deep/dir/f_$i.txt; done
for i in $(seq 1 2500); do echo $i > src/many/file_with_long_name_$i; done
ln -s ../etc/a.txt src/bin/short; ln -s /$(printf 'x%.0s' $(seq 1 200))/target src/bin/long
ln src/etc/a.txt src/bin/hard
setfattr -n user.test -v valueX src/etc/a.txt
setfattr -n security.selinux -v u:object_r:system_file:s0 src/etc/a.txt
mknod src/etc/cdev c 1 3; mkfifo src/etc/fifo
fail=0
check() { out=$(EXCL="$EXCL" "$V" "$1" src | grep -v "cdev\|fifo\|fstab" || true); if [ -n "$out" ]; then echo "FAIL $2: $out"; fail=1; else echo "ok   $2"; fi; }
for opt in "plain:" "lz4:-zlz4" "lz4hc:-zlz4hc,9 -C65536" "ztail:-zlz4hc -Eztailpacking" "frag:-zlz4hc -Efragments -C131072" \
           "dedupe:-zlz4hc -Efragments,dedupe -C65536" "deflate:-zdeflate" "allfrag:-zlz4 -C16384 -Eall-fragments" "big:-zlz4hc -C262144 -Eztailpacking"; do
  n=${opt%%:*}; o=${opt#*:}
  mkfs.erofs $o $n.erofs src >/dev/null 2>&1
  $T fs2ext4 $n.erofs $n.ext4 >/dev/null
  check $n.ext4 "erofs-$n"
done
for spec in "e1:-t ext4 -O ^has_journal" "e2:-t ext4 -O 64bit,metadata_csum,inline_data,^has_journal" "e3:-t ext4 -b 1024" "e4:-t ext2 -I 128"; do
  n=${spec%%:*}; o=${spec#*:}
  mke2fs -q $o -d src $n.img 80M >/dev/null 2>&1
  $T fs2ext4 $n.img $n.ext4 >/dev/null
  check $n.ext4 "ext4-$n"
done
# full super pipeline
head -c 5000000 /dev/urandom > odm.raw
$T mksuper super.raw 260 system=ztail.erofs vendor=lz4hc.erofs product=e1.img odm=odm.raw >/dev/null
img2simg super.raw super.sparse.img
mkdir -p ex_orig && python3 "$ROOT/tests/lpcheck.py" super.raw ex_orig > lp_orig.txt
rm -rf outd ex; mkdir -p outd ex
$T super super.sparse.img outd sparse >/dev/null
simg2img outd/super_rw.img outd/back.raw
python3 "$ROOT/tests/lpcheck.py" outd/back.raw ex >/dev/null && echo "ok   lp-metadata" || { echo "FAIL lp-metadata"; fail=1; }
for p in system_a product_a; do check ex/$p.img "super-$p"; done
EXCL="fstab*" check ex/vendor_a.img "super-vendor_a"
cmp -n 5000000 ex/odm_a.img odm.raw >/dev/null && echo "ok   super-copy-raw" || { echo "FAIL super-copy-raw"; fail=1; }
debugfs -R "cat /etc/fstab.qcom" ex/vendor_a.img 2>/dev/null | grep -q "/vendor ext4" && echo "ok   fstab-patch" || { echo "FAIL fstab-patch"; fail=1; }
# ---- Magisk / LK / vbmeta
mkdir -p mg && cd mg
mkdir -p apk/lib/arm64-v8a apk/assets
head -c 300000 /dev/urandom > apk/lib/arm64-v8a/libmagiskinit.so
head -c 900000 /dev/urandom > apk/lib/arm64-v8a/libmagisk.so
head -c 50000 /dev/urandom > apk/lib/arm64-v8a/libinit-ld.so
mkdir -p apk/lib/armeabi-v7a
head -c 200000 /dev/urandom > apk/lib/armeabi-v7a/libmagiskinit.so
head -c 700000 /dev/urandom > apk/lib/armeabi-v7a/libmagisk.so
head -c 40000 /dev/urandom > apk/lib/armeabi-v7a/libinit-ld.so
printf 'add 0644 overlay.d/sbin/magisk.xz magisk.xz\nadd 0644 overlay.d/sbin/init-ld.xz\necho VENDORBOOT\n' > apk/assets/boot_patch.sh
(cd apk && python3 -c "
import zipfile,os
z=zipfile.ZipFile('../Magisk-test.apk','w',zipfile.ZIP_DEFLATED)
for r,_,fs in os.walk('.'):
  for f in fs: z.write(os.path.join(r,f), os.path.relpath(os.path.join(r,f),'.'))
z.close()")
for spec in "v2gz:2 gzip mtk avb" "v4lz4:4 lz4" "v0raw:0 raw" "v1lz4mtk:1 lz4 mtk" "v3gz:3 gzip avb"; do
  n=${spec%%:*}; args=${spec#*:}
  python3 "$ROOT/tests/mkboot.py" $n.img $args
  if $T magisk $n.img Magisk-test.apk $n.p.img >/dev/null && python3 "$ROOT/tests/checkboot.py" $n.p.img >/dev/null \
     && cmp -s <(xz -dc x/overlay.d/sbin/magisk.xz) apk/lib/arm64-v8a/libmagisk.so \
     && cmp -s x/init apk/lib/arm64-v8a/libmagiskinit.so \
     && xz -dc x/.backup/init.xz | grep -q ORIGINAL-INIT \
     && grep -q "KEEPVERITY=false" x/.backup/.magisk \
     && ! grep -q "verify\|avb=\|forceencrypt" x/fstab.mt6765 && [ ! -e x/verity_key ]; then
    echo "ok   magisk-$n"
  else echo "FAIL magisk-$n"; fail=1; fi
done
# 32-bit userspace (ARM ELF init) must get armeabi-v7a binaries even with a 64-bit kernel
python3 "$ROOT/tests/mkboot.py" v2arm32.img 2 gzip mtk avb arm32
if $T magisk v2arm32.img Magisk-test.apk v2arm32.p.img >/dev/null && python3 "$ROOT/tests/checkboot.py" v2arm32.p.img >/dev/null \
   && cmp -s x/init apk/lib/armeabi-v7a/libmagiskinit.so && cmp -s <(xz -dc x/overlay.d/sbin/magisk.xz) apk/lib/armeabi-v7a/libmagisk.so; then
  echo "ok   magisk-abi-arm32"; else echo "FAIL magisk-abi-arm32"; fail=1; fi
# only a real LK (MTK header name "lk") is classified as LK
python3 -c "
import struct,os
for name,fn in ((b'lk','lk_hdr.bin'),(b'logo','logo.bin'),(b'md1rom','md1img.img'),(b'tinysys-scp','scp.img')):
    open(fn,'wb').write(struct.pack('<II',0x58881688,4000)+name.ljust(32,b'\0')+os.urandom(4000))"
c1=$($T classify lk_hdr.bin); c2=$($T classify logo.bin); c3=$($T classify md1img.img); c4=$($T classify scp.img)
[ "$c1" = "lk|Lk" ] && [ "$c2" = "|None" ] && [ "$c3" = "|None" ] && [ "$c4" = "|None" ] && echo "ok   lk-classify" || { echo "FAIL lk-classify: $c1 $c2 $c3 $c4"; fail=1; }
python3 -c "
import os,struct
d=bytearray(struct.pack('<II',0x58881688,0)+os.urandom(5000))
d+=bytes.fromhex('08B5AB127B441B681B68022B')+os.urandom(3000)
d+=b'Orange State\x00\x00Your device has been unlocked and can\'t be trusted\x00Your device will boot in 5 seconds\x00OTHER STRING KEPT\x00'
open('lk.img','wb').write(d)
open('vbmeta.img','wb').write(b'AVB0'+bytes(2000))"
$T lk lk.img lk.out >/dev/null
python3 -c "
d=open('lk.out','rb').read()
assert d.find(bytes.fromhex('08B5002008BD1B681B68022B'))>0 and b'Orange' not in d and b'OTHER STRING KEPT' in d" && echo "ok   lk-patch" || { echo "FAIL lk-patch"; fail=1; }
$T vbmeta vbmeta.img vb.out >/dev/null
python3 -c "assert open('vb.out','rb').read()[123]==3" && echo "ok   vbmeta-patch" || { echo "FAIL vbmeta-patch"; fail=1; }
# edited-folder scatter from a stock MTK scatter: only edited partitions enabled
mkdir -p sc/ערוך && cp "$ROOT/tests/stock_scatter.txt" sc/MT6765_Android_scatter.txt
echo x > sc/ערוך/lk.img; echo x > sc/ערוך/vbmeta.img
$T scatter "$PWD/sc" >/dev/null
python3 - <<'PY' && echo "ok   edited-scatter" || { echo "FAIL edited-scatter"; fail=1; }
import re
t=open('sc/ערוך/MT6765_Android_scatter.txt').read()
dl={m.group(1):m.group(2) for m in re.finditer(r'partition_name: (\S+)\n  file_name: \S+\n  is_download: (\w+)', t)}
assert dl=={'preloader':'false','lk':'true','boot':'false','vbmeta':'true','logo':'false','userdata':'false'}, dl
PY
# flash back with a fake mtkclient: name mapping via gpt.bin, sparse expanded, sensitive partitions blocked
mkdir -p fl/ed "$W/tools/mtkclient"
cat > "$W/tools/mtkclient/mtk.exe" <<'SH'
#!/bin/bash
IFS=',' read -ra P <<< "$2"; IFS=',' read -ra F <<< "$3"
for i in "${!P[@]}"; do cp "${F[$i]}" "$(dirname "$0")/written_${P[$i]}.bin"; done
echo "$*" > "$(dirname "$0")/args.txt"
SH
chmod +x "$W/tools/mtkclient/mtk.exe"
cp v2gz.img fl/ed/boot.img; cp vbmeta.img fl/ed/vbmeta.img; cp ../super.sparse.img fl/ed/super.img; head -c 1000 /dev/urandom > fl/ed/preloader.bin
truncate -s 400M fl/disk.img
sgdisk -o -n 1:2048:+32M -c 1:boot_a -n 2:0:+1M -c 2:vbmeta_a -n 3:0:+270M -c 3:super -n 4:0:+1M -c 4:preloader fl/disk.img >/dev/null
head -c $((34*512)) fl/disk.img > fl/gpt.bin
$T flash "$PWD/fl/ed" "$PWD/fl/gpt.bin" >/dev/null
M="$W/tools/mtkclient"
if grep -q "^w boot_a,super,vbmeta_a " "$M/args.txt" && cmp -s "$M/written_super.bin" ../super.raw && cmp -s "$M/written_boot_a.bin" v2gz.img \
   && [ ! -e "$M/written_preloader.bin" ] && [ ! -d fl/ed/_flash_tmp ]; then echo "ok   flash-back"; else echo "FAIL flash-back: $(cat "$M/args.txt")"; fail=1; fi
# fast engine (alir): fake mtkflash.exe exercises dump + scatter-flash wiring (cwd = mg)
FT="$(dirname "$W/t.exe")/tools/mtkflash"; mkdir -p "$FT"; : > "$FT/DA_PL.bin"; MG="$PWD"
cat > "$FT/mtkflash.exe" <<SH
#!/bin/bash
cmd="\$1"; shift
declare -A A; while [ \$# -gt 0 ]; do case "\$1" in --*) k="\${1:2}"; shift; A[\$k]="\$1";; esac; shift; done
if [ "\$cmd" = dumpall ]; then
  cp "$MG/fl/gpt.bin" "\${A[out]}/gpt.bin"
  cp "$MG/v2gz.img" "\${A[out]}/boot_a.img"; cp "$MG/vbmeta.img" "\${A[out]}/vbmeta_a.img"; cp "$MG/../super.sparse.img" "\${A[out]}/super.img"
  echo "Chip: MT6765"
elif [ "\$cmd" = flash ]; then
  grep -E "partition_name|file_name|is_download" "\${A[scatter]}" | paste - - - | grep -c "is_download: true" > "$MG/fe_nflash.txt"
fi
SH
chmod +x "$FT/mtkflash.exe"; mkdir -p fe_proj fe_ed
$T fastdump "$MG/fe_proj" >/dev/null 2>&1
if [ -f fe_proj/gpt.bin ] && [ -f fe_proj/boot_a.img ] && ls fe_proj/*_scatter.txt >/dev/null 2>&1 && [ "$(grep -c 'is_download: true' fe_proj/*_scatter.txt)" = 3 ]; then echo "ok   fast-dump"; else echo "FAIL fast-dump"; fail=1; fi
cp v2gz.img fe_ed/boot_a.img; cp vbmeta.img fe_ed/vbmeta_a.img; cp ../super.sparse.img fe_ed/super.img
$T fastflash "$MG/fe_ed" "$MG/fl/gpt.bin" >/dev/null 2>&1
if [ "$(cat fe_nflash.txt 2>/dev/null)" = 3 ] && [ ! -d fe_ed/_fastflash ] && [ ! -d fe_ed/_flash_tmp ]; then echo "ok   fast-flash"; else echo "FAIL fast-flash"; fail=1; fi
# SP Flash Tool console config: only is_download=true roms, download-only (no <format> tag)
mkdir -p spx; printf '%s' '- general:
    - platform: MT6765
      storage: EMMC
- partition_index: SYS0
  partition_name: preloader
  file_name: NONE
  is_download: false
- partition_index: SYS1
  partition_name: boot
  file_name: boot.img
  is_download: true
- partition_index: SYS2
  partition_name: vbmeta
  file_name: vbmeta.img
  is_download: true
' > spx/s.txt
echo b > spx/boot.img; echo v > spx/vbmeta.img; echo DA > spx/da.bin
$T spconfig "$PWD/spx/s.txt" "$PWD/spx" "$PWD/spx/da.bin" >/dev/null 2>&1
if [ -f spx/sp_flash_config.xml ] \
   && [ "$(grep -c '<rom ' spx/sp_flash_config.xml)" = 2 ] \
   && grep -q 'da-download-all' spx/sp_flash_config.xml \
   && ! grep -qE '<format' spx/sp_flash_config.xml \
   && ! grep -q 'preloader' spx/sp_flash_config.xml; then echo "ok   sp-config"; else echo "FAIL sp-config"; fail=1; fi
# WiFi removal: delete /vendor/bin/hw/wpa_supplicant from super, keep XML, save backup
mkdir -p wsrc/bin/hw wsrc/etc/permissions
printf 'ELF-WPA' > wsrc/bin/hw/wpa_supplicant
printf '<permissions><feature name="android.hardware.wifi"/></permissions>' > wsrc/etc/permissions/handheld.xml
mke2fs -q -t ext4 -O ^has_journal -d wsrc wvendor.img 40M >/dev/null 2>&1
$T mksuper wsuper.raw 120 vendor=wvendor.img >/dev/null; img2simg wsuper.raw wsuper.sparse.img
mkdir -p wout wex; $T super wsuper.sparse.img wout sparse nowifi >/dev/null 2>&1
simg2img wout/super_rw.img wout/back.raw; python3 "$ROOT/tests/lpcheck.py" wout/back.raw wex >/dev/null
if debugfs -R "stat /bin/hw/wpa_supplicant" wex/vendor_a.img 2>&1 | grep -q "File not found" \
   && debugfs -R "stat /etc/permissions/handheld.xml" wex/vendor_a.img 2>&1 | grep -q "Inode:" \
   && cmp -s wout/wpa_supplicant.backup wsrc/bin/hw/wpa_supplicant \
   && e2fsck -fn wex/vendor_a.img >/dev/null 2>&1; then echo "ok   remove-wifi"; else echo "FAIL remove-wifi"; fail=1; fi
# vbmeta modes: 1 = verity only, 2 = verification only
$T vbmeta2 vbmeta.img vb1.out 1 >/dev/null; $T vbmeta2 vbmeta.img vb2.out 2 >/dev/null
python3 -c "assert open('vb1.out','rb').read()[123]==1 and open('vb2.out','rb').read()[123]==2" && echo "ok   vbmeta-modes" || { echo "FAIL vbmeta-modes"; fail=1; }
# LK manual Android version: the lk.img pattern is Android 10+ (02) -> "9" finds nothing, "10" patches
d9=$($T lk2 lk.img lk9.out 9 | grep -oE "delay=[0-9]+" | tail -1); d10=$($T lk2 lk.img lk10.out 10 | grep -oE "delay=[0-9]+" | tail -1)
[ "$d9" = "delay=0" ] && [ "$d10" = "delay=1" ] && echo "ok   lk-android-version" || { echo "FAIL lk-android-version: $d9 $d10"; fail=1; }
# lk dm_verity/fastboot/red_state neutering (source: lkpatcher patterns) -> 00207047, length preserved
python3 -c "
import os,struct
d=bytearray(struct.pack('<II',0x58881688,0)+b'lk'.ljust(32,b'\0')+os.urandom(1000))
d+=bytes.fromhex('30b583b002ab0022')+os.urandom(300)
d+=bytes.fromhex('022800d0704738b53224e0f7')+os.urandom(300)
d+=bytes.fromhex('f0b5adf5925d')+os.urandom(300)
d+=bytes.fromhex('f0b5002489b0')+os.urandom(300)
open('lkw.img','wb').write(d)"
$T lk lkw.img lkw.out >/dev/null 2>&1
python3 -c "
a=open('lkw.img','rb').read(); b=open('lkw.out','rb').read()
ok = len(a)==len(b)
for pat in ('30b583b002ab0022','022800d0704738b53224e0f7','f0b5adf5925d','f0b5002489b0'):
    off=a.find(bytes.fromhex(pat)); ok = ok and b[off:off+4].hex()=='00207047'
import sys; sys.exit(0 if ok else 1)" && echo "ok   lk-dmverity" || { echo "FAIL lk-dmverity"; fail=1; }
# generic hex replace with wildcards (?? keeps bytes)
python3 -c "open('hx.bin','wb').write(bytes.fromhex('00112233AABBCCDD0011223344'))"
$T hex hx.bin hx.out "0011??33" "FF??EE??" all >/dev/null
python3 -c "assert open('hx.out','rb').read()==bytes.fromhex('FF11EE33AABBCCDDFF11EE3344')" && echo "ok   hex-replace" || { echo "FAIL hex-replace"; fail=1; }
# Magisk PATCHVBMETAFLAG + RECOVERYMODE
$T magisk2 v3gz.img Magisk-test.apk v3m.img >/dev/null && python3 "$ROOT/tests/checkboot.py" v3m.img >/dev/null
python3 - <<'PY' && grep -q "RECOVERYMODE=true" x/.backup/.magisk && echo "ok   magisk-options" || { echo "FAIL magisk-options"; fail=1; }
import struct
d=open('v3m.img','rb').read()
off=struct.unpack('>Q', d[len(d)-64+20:len(d)-64+28])[0]
assert d[off:off+4]==b'AVB0' and d[off+123]&3==3
PY
cd ..
# per-partition plan (SuperPlan): system +20MB converted, vendor copied as-is, product +5MB
mkdir -p plan
cp super.sparse.img plan/super.img
printf 'system_a=convert,20\nvendor_a=copy,0\nproduct_a=convert,5\n' > plan/super.img.ro2rw-plan.txt
$T project "$PWD/plan" mg/Magisk-test.apk >/dev/null
simg2img "plan/ערוך/super.img" plan/out.raw; mkdir -p plan/ex
python3 "$ROOT/tests/lpcheck.py" plan/out.raw plan/ex > plan/lp.txt
python3 - <<'PY' && echo "ok   super-plan" || { echo "FAIL super-plan"; cat plan/lp.txt; fail=1; }
import re
t=open('plan/lp.txt').read()
size={m.group(1):int(m.group(2)) for m in re.finditer(r'(\w+)\s+attr=\d+ group=\d+ size=(\d+)', t)}
orig={m.group(1):int(m.group(2)) for m in re.finditer(r'(\w+)\s+attr=\d+ group=\d+ size=(\d+)', open('lp_orig.txt').read())}
assert size['vendor_a']==orig['vendor_a'], ('vendor changed', size, orig)
assert open('plan/ex/vendor_a.img','rb').read()==open('ex_orig/vendor_a.img','rb').read()
import subprocess
fs=subprocess.run(['dumpe2fs','-h','plan/ex/system_a.img'],capture_output=True,text=True).stdout
free=int(re.search(r'Free blocks:\s+(\d+)',fs).group(1))*4096
assert 19<<20 <= free <= 23<<20, free
PY
exit $fail
