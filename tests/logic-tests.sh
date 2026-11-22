#!/bin/bash
# Pure-logic tests (no external tools). Usage: logic-tests.sh "<command that runs the test exe>"
#   Linux: logic-tests.sh "mono /tmp/t/t.exe"      Windows: logic-tests.sh "./t.exe"
T=${1:?usage: logic-tests.sh "<test exe command>"}
fail=0; n=0
check() { # check <name> <expected> <actual>
  n=$((n+1))
  if [ "$2" == "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected [$2] got [$3]"; fail=$((fail+1)); fi
}
tag() { $T magisktag "$@" | tr -d '\r'; }
pump() { $T pumplines "$1" | tr -d '\r' | paste -sd, -; }

check "tag major resolves to newest 26.x"   "v26.4"  "$(tag 26 v27.0 v26.4 v26.3 v25.2)"
check "tag v-prefixed major"                "v26.4"  "$(tag v26 v27.0 v26.4 v26.3)"
check "tag exact minor"                     "v26.3"  "$(tag 26.3 v26.4 v26.3)"
check "tag exact with v"                    "v26.4"  "$(tag v26.4 v26.4 v26.3)"
check "tag missing minor"                   "null"   "$(tag 26.9 v26.4 v26.3)"
check "tag missing major"                   "null"   "$(tag 30 v27.0 v26.4)"
check "tag ignores canary"                  "v26.1"  "$(tag 26 canary-27001 v26.1)"
check "tag numeric order 26.10 > 26.9"      "v26.10" "$(tag 26 v26.9 v26.10 v26.2)"

check "pump CRLF lines go to the log"       "L:Begin,L:Download Succeeded." "$(pump 'Begin\r\nDownload Succeeded.\r\n')"
check "pump LF lines go to the log"         "L:a,L:b" "$(pump 'a\nb\n')"
check "pump CRLF percent line is status"    "S:5% of image data has been sent" "$(pump '5% of image data has been sent\r\n')"
check "pump CR-only progress is status"     "S:10%,S:20%,S:30%" "$(pump '10%\r20%\r30%\n')"
check "pump mixed"                          "L:Connecting,S:50% done,L:OK" "$(pump 'Connecting\r\n50% done\r\nOK\r\n')"

BK=$(mktemp -d); bk() { $T backuptest "$BK" | tr -d "" | grep "^$1:" | head -1; }
check "backup candidates skip gpt and scatter" "candidates:a.img,b.img" "$(bk candidates)"
check "backup sha256 of AAAA" "hash:63c1dd951ffedf6f7fd968ad4efa39b8ed584f162f46e715114ee184f8de9201" "$(bk hash)"
check "backup manifest roundtrip" "roundtrip:24,2026-01-01T00:00:00.0000000Z,2" "$(bk roundtrip)"
check "backup verify clean" "verify1:" "$(bk verify1)"
check "backup restore set = originals with an edited twin" "restoreset:a.img" "$(bk restoreset)"
check "backup verify detects changed file" "verify2:b.img:hash" "$(bk verify2)"
check "backup verify only the restore set" "verify-only-a:" "$(bk verify-only-a)"
check "backup expiry boundary and never" "expired:False,True,False" "$(bk expired)"
check "backup remaining time" "remaining:18,True" "$(bk remaining)"
check "backup expire deletes only listed originals" "expire:2 gpt=True edited=True manifest=False a=False" "$(bk expire)"
mc() { $T mustconvert "$@" | tr -d '\r'; }
check "vendor ext4 must convert"             "True"  "$(mc vendor EXT4 100 vendor system)"
check "vendor erofs must convert"            "True"  "$(mc vendor EROFS 100 vendor)"
check "vendor_a must convert"                "True"  "$(mc vendor_a EROFS 100 vendor_a vendor_b system_a)"
check "vendor_b with vendor_a is copied"     "False" "$(mc vendor_b EROFS 100 vendor_a vendor_b)"
check "vendor_b alone must convert"          "True"  "$(mc vendor_b EROFS 100 vendor_b)"
check "raw vendor cannot be converted"       "False" "$(mc vendor RAW 100 vendor)"
check "empty vendor is skipped"              "False" "$(mc vendor EXT4 0 vendor)"
check "system is not forced"                 "False" "$(mc system EROFS 100 system vendor)"

sdt() { rm -rf "$SD"; mkdir -p "$SD"; $T spdumptest "$SD" | tr -d '\r' | grep "^$1:" | head -1; }
SD=$(mktemp -d)
check "gpt check: real / zeros / null"          "gptok:True,False,False" "$(sdt gptok)"
check "full-dump length ends at last selected"  "range:98000" "$(sdt range)"
check "split cuts partitions, skips truncated"  "split:boot_a,super skipped=userdata" "$(sdt split)"
check "split content is byte exact"             "content:32768,True,524288,True,False" "$(sdt content)"
check "edit-partition preset names"             "preset:True,False,True,False,True,True,True,True" "$(sdt preset)"
check "range readback config attributes"        "rangecfg:True,True,True,True,False" "$(sdt rangecfg)"
check "range config with a scatter adds a disabled rom entry" "rangecfg2:True,True,True" "$(sdt rangecfg2)"
check "flash-by-name config (no scatter)"       "flashcfg:True,True,True,False" "$(sdt flashcfg)"
check "scatter from gpt lists all partitions"   "scatter:pgpt,boot_a,lk_a,super,userdata,sgpt" "$(sdt scatter)"
check "scatter flags only dumped as download"   "scatterdl:boot_a,super" "$(sdt scatterdl)"
check "scatter keeps real start and size"       "scattersize:18000,80000" "$(sdt scattersize)"
check "SP flash list reads the new scatter"     "enabled:2" "$(sdt enabled)"
check "minimal scatter has only pgpt"           "minimal:pgpt,MT6765_empty_scatter.txt" "$(sdt minimal)"
check "part-id candidates (SP sample first)"    "partids:34,8|34,2,3|32,1" "$(sdt partids)"
check "preloader magic check"                   "preloader:True,False,False" "$(sdt preloader)"

check "range over 4 GiB is split (SP length is 32-bit)" "chunks:6,True,True,1" "$(sdt chunks)"

echo "Ran $n logic tests, $fail failed"
[ $fail -eq 0 ]
