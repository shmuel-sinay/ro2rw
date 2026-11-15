#!/bin/bash
# usage: verify.sh image.ext4 srcdir
img=$1; src=$2; out=$(mktemp -d)
e2fsck -fn $img > $out.fsck 2>&1; rc=$?
[ $rc -ne 0 ] && { echo "  e2fsck rc=$rc"; head -20 $out.fsck; }
debugfs -R "rdump / $out" $img >/dev/null 2>&1
d=$(diff -r --no-dereference ${EXCL:+-x "$EXCL"} $src $out 2>&1 | grep -v "lost+found" | head -5)
[ -n "$d" ] && echo "  DIFF: $d"
ea=$(debugfs -R "ea_list /etc/a.txt" $img 2>/dev/null | tr '\n' ' ')
echo "$ea" | grep -q 'security.selinux' && echo "$ea" | grep -q 'user.test' || echo "  XATTR missing: $ea"
debugfs -R "stat /bin/hard" $img 2>/dev/null | grep -q "Links: 2" || echo "  hardlink count wrong"
debugfs -R "stat /etc/cdev" $img 2>/dev/null | grep -qi "device major/minor number: 01:03\|major/minor.*1.*3" || echo "  cdev: $(debugfs -R 'stat /etc/cdev' $img 2>/dev/null | grep -i device)"
[ "$(readlink $out/bin/long)" = "$(readlink $src/bin/long)" ] || echo "  long symlink wrong"
rm -rf $out $out.fsck
