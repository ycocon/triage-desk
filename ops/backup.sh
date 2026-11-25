#!/usr/bin/env bash
set -euo pipefail
: "${PGHOST:?Set the direct PostgreSQL host}"
: "${PGDATABASE:?Set the PostgreSQL database}"
: "${PGUSER:?Set the read-only backup role}"
: "${PGPASSWORD:?Set the backup-role credential}"
: "${AGE_RECIPIENT:?Set the independent age public recipient}"
: "${R2_ENDPOINT:?Set the R2 S3 endpoint}"
: "${R2_BUCKET:?Set the private R2 bucket}"
: "${BACKUP_HEARTBEAT:?Set the success heartbeat URL}"
[[ "$PGHOST" != *-pooler* && "$R2_ENDPOINT" =~ ^https://[a-z0-9]+\.r2\.cloudflarestorage\.com/?$ ]]
[[ "$BACKUP_HEARTBEAT" =~ ^https://hc-ping\.com/[A-Za-z0-9_-]+$ ]]
export PGSSLMODE=verify-full
export AWS_DEFAULT_REGION=auto
export AWS_REQUEST_CHECKSUM_CALCULATION=when_required

tmp=$(mktemp -d)
cleanup() { rm -rf -- "$tmp"; }
trap cleanup EXIT
psql --version | grep -Eq ' 18\.'
pg_dump --version | grep -Eq ' 18\.'

IFS='|' read -r snapshot version migration < <(
  psql -X -v ON_ERROR_STOP=1 -At -F '|' -c "
    SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"'),
           current_setting('server_version'), max(\"MigrationId\") FROM \"__EFMigrationsHistory\""
)
[[ "$version" == 18.* && -n "$migration" ]]
pg_dump --format=custom --no-owner --no-privileges --file="$tmp/database.dump"
pg_restore --list "$tmp/database.dump" > "$tmp/archive.list"
grep -q 'TABLE DATA public IssueTriage' "$tmp/archive.list"
age --encrypt --recipient "$AGE_RECIPIENT" --output "$tmp/database.dump.age" "$tmp/database.dump"
hash=$(sha256sum "$tmp/database.dump.age" | cut -d ' ' -f 1)
plain_hash=$(sha256sum "$tmp/database.dump" | cut -d ' ' -f 1)
object="postgres/${snapshot:0:10}/${snapshot}-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}"
aws --endpoint-url "$R2_ENDPOINT" s3api put-object --bucket "$R2_BUCKET" --key "$object.dump.age" \
  --body "$tmp/database.dump.age" --if-none-match '*' > /dev/null
aws --endpoint-url "$R2_ENDPOINT" s3api get-object --bucket "$R2_BUCKET" --key "$object.dump.age" \
  "$tmp/download.age" > /dev/null
[[ "$(sha256sum "$tmp/download.age" | cut -d ' ' -f 1)" == "$hash" ]]
jq -n --arg snapshot "$snapshot" --arg version "$version" --arg migration "$migration" \
  --arg key "$object.dump.age" --arg sha256 "$hash" --arg archiveSha256 "$plain_hash" \
  --argjson bytes "$(wc -c < "$tmp/database.dump.age")" \
  '{snapshotUtc:$snapshot, postgresVersion:$version, migration:$migration, objectKey:$key,
    ciphertextSha256:$sha256, archiveSha256:$archiveSha256, bytes:$bytes}' > "$tmp/manifest.json"
aws --endpoint-url "$R2_ENDPOINT" s3api put-object --bucket "$R2_BUCKET" --key "$object.json" \
  --body "$tmp/manifest.json" --content-type application/json --if-none-match '*' > /dev/null
bash ops/check-backup-age.sh "$snapshot" 18
curl --silent --fail --retry 3 --max-time 20 "$BACKUP_HEARTBEAT" > /dev/null
printf 'Verified encrypted backup and manifest %s.json\n' "$object"
