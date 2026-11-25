#!/usr/bin/env bash
set -euo pipefail
snapshot=${1:?Provide the source snapshot timestamp}
limit=${2:-18}
[[ "$snapshot" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ && "$limit" =~ ^[0-9]+$ ]]
now=${TRIAGEDESK_CHECK_EPOCH:-$(date -u +%s)}
created=$(date -u -d "$snapshot" +%s)
age=$(( now - created ))
if (( age < 0 || age >= limit * 3600 )); then
  printf 'Backup outside the %s-hour recovery window.\n' "$limit" >&2
  exit 1
fi
printf 'Verified snapshot age is %s seconds.\n' "$age"
