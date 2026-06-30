#!/usr/bin/env bash
# Register (or update) the Live Now web-tab inject with the File Transformation plugin.
# Run from a host that can reach Jellyfin. Requires: JF base URL + API key.
#
#   JF=http://localhost:8096/jellyfin  KEY=<apikey>  ./register.sh
#
# Idempotent: re-POSTing replaces the FT transformation list with just our one entry.
# To remove the tab, POST {"DebugLoggingState":"Disabled","Transformations":[]} instead.
set -euo pipefail

JF="${JF:-http://localhost:8096/jellyfin}"
KEY="${KEY:?set KEY to the Jellyfin API key}"
FT_GUID="5e87cc92571a4d8d8d98d2d4147f9f90"
INJECT_ID="11ee0000-1abe-4a0e-9001-11feed000001"   # MUST be a GUID (non-GUID Id -> HTTP 500)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

CFG="$(python3 - "$SCRIPT_DIR/livenow-tab-inject.js" "$INJECT_ID" <<'PY'
import json, sys
script = open(sys.argv[1]).read()
inject = "<script defer>\n" + script + "\n</script></body></html>"
print(json.dumps({
    "DebugLoggingState": "Disabled",
    "Transformations": [{
        "Id": sys.argv[2],
        "FilenamePattern": "index.html",
        "SearchText": "</body></html>",
        "ReplaceText": inject,
    }],
}, ensure_ascii=True))
PY
)"

code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$JF/Plugins/$FT_GUID/Configuration" \
  -H "Authorization: MediaBrowser Token=\"$KEY\"" \
  -H 'Content-Type: application/json' \
  --data-binary "$CFG")"
echo "POST FT config -> HTTP $code"
[ "$code" = "204" ] || { echo "FAILED"; exit 1; }
echo "OK — Live Now tab inject registered. (FT applies on the fly; no restart needed.)"
