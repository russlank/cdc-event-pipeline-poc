#!/usr/bin/env bash
# Checks whether CDC cleanup has removed changes Debezium has not read yet: compares the offset
# Debezium stored in Kafka Connect with the low watermark (the oldest change kept) of every capture
# instance. Run it before starting Connect after an outage longer than the CDC retention
# (sys.sp_cdc_help_jobs, cleanup retention in minutes); while Connect runs, its offset moves past
# the watermark again and the gap can no longer be seen here.
# Exit 0: no capture instance was cleaned past the offset. Exit 1: any change between the offset and
# the watermark of the listed instances is gone, and Debezium refuses to start from the offset;
# repair with scripts/ops/resnapshot.sh.

source "$(dirname "$0")/../scenarios/lib.sh"

offset=$(last_message _connect-offsets | cut -f 2)
if [ "$offset" = null ]; then
  # Deleted offsets (scripts/ops/resnapshot.sh) until the connector stores new ones.
  pass "no stored offset: the connector takes a new snapshot, nothing can be missing"
  exit 0
fi
commit_lsn=$(echo "$offset" | sed -n 's/.*"commit_lsn":"\([0-9a-f:]*\)".*/\1/p')
[ -n "$commit_lsn" ] || fail "no stored Debezium offset with a commit LSN"
lsn_hex="0x$(echo "$commit_lsn" | tr -d ':' | tr '[:lower:]' '[:upper:]')"
echo "stored offset: commit LSN $commit_lsn"

# A capture instance has lost changes when its low watermark is above the offset: cleanup removed
# everything below the watermark, including changes after the offset.
rows=$(sql_rows "SELECT CONCAT(capture_instance, ' ', CONVERT(varchar(22), sys.fn_cdc_get_min_lsn(capture_instance), 1), ' ',
    CASE WHEN sys.fn_cdc_get_min_lsn(capture_instance) > CONVERT(binary(10), '$lsn_hex', 1) THEN 'GAP' ELSE 'ok' END)
  FROM cdc.change_tables ORDER BY capture_instance")
echo "$rows" | awk '{ printf "%-24s low watermark %s  %s\n", $1, $2, $3 }'
if echo "$rows" | grep -q ' GAP$'; then
  fail "CDC cleanup passed the stored offset: changes Debezium has not read are gone; repair with scripts/ops/resnapshot.sh"
fi
pass "every capture instance still holds the changes after the stored offset"
