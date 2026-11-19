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

echo "Ran $n logic tests, $fail failed"
[ $fail -eq 0 ]
