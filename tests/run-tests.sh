#!/bin/bash
# Core tests (Linux): requires mono-mcs, erofs-utils, e2fsprogs, android-sdk-libsparse-utils, attr
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
W=${1:-$(mktemp -d)}
mkdir -p "$W"
mcs -nologo -out:"$W/t.exe" "$ROOT"/src/Core/*.cs "$ROOT/tests/TestMain.cs"
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
rm -rf outd ex; mkdir -p outd ex
$T super super.sparse.img outd sparse >/dev/null
simg2img outd/super_rw.img outd/back.raw
python3 "$ROOT/tests/lpcheck.py" outd/back.raw ex >/dev/null && echo "ok   lp-metadata" || { echo "FAIL lp-metadata"; fail=1; }
for p in system_a product_a; do check ex/$p.img "super-$p"; done
EXCL="fstab*" check ex/vendor_a.img "super-vendor_a"
cmp -n 5000000 ex/odm_a.img odm.raw >/dev/null && echo "ok   super-copy-raw" || { echo "FAIL super-copy-raw"; fail=1; }
debugfs -R "cat /etc/fstab.qcom" ex/vendor_a.img 2>/dev/null | grep -q "/vendor ext4" && echo "ok   fstab-patch" || { echo "FAIL fstab-patch"; fail=1; }
exit $fail
