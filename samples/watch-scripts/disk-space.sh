#!/bin/sh
# Example script watch: warns when a filesystem is getting full.
#
#   avamux watch add "Disk" --kind command --interval 10 \
#       --set "command=/path/to/disk-space.sh" --set mount=/home --set limit=85
#
# Settings arrive as AVAMUX_WATCH_<KEY> environment variables. Print one JSON object:
#   {"status":"ok|info|warn|error","summary":"…","link":"…","items":[{"title":"…","status":"…","detail":"…","link":"…"}]}
mount="${AVAMUX_WATCH_MOUNT:-/}"
limit="${AVAMUX_WATCH_LIMIT:-90}"

used=$(df -P "$mount" | awk 'NR==2 { gsub("%", "", $5); print $5 }')
free=$(df -Ph "$mount" | awk 'NR==2 { print $4 }')
if [ -z "$used" ]; then
  echo "{\"status\":\"error\",\"summary\":\"Could not read disk usage of $mount\"}"
  exit 0
fi

status=ok
[ "$used" -ge "$limit" ] && status=warn
[ "$used" -ge 97 ] && status=error
echo "{\"status\":\"$status\",\"summary\":\"$mount is ${used}% full ($free free)\",\"items\":[{\"title\":\"$mount\",\"status\":\"$status\",\"detail\":\"${used}% used, limit ${limit}%\"}]}"
