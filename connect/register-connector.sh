#!/bin/sh
# Registers or updates a Kafka Connect connector and waits until it and its tasks run.
# Idempotent: PUT /connectors/<name>/config creates or replaces the configuration.
#
# Usage: register-connector.sh <connect-url> <connector.json>
#   <connector.json> holds {"name": "...", "config": {...}}.
# Runs in the curlimages/curl container (POSIX sh, curl, sed) or any shell with curl.

set -eu

connect_url=${1:?usage: register-connector.sh <connect-url> <connector.json>}
file=${2:?usage: register-connector.sh <connect-url> <connector.json>}

name=$(sed -n 's/^ *"name": *"\([^"]*\)".*/\1/p' "$file" | head -n 1)
[ -n "$name" ] || { echo "no \"name\" in $file" >&2; exit 2; }

# The PUT body is the inner "config" object: from the '"config": {' line to its closing
# brace at two-space indent (the layout of the connector files in this folder).
config=$(sed -n '/"config": *{/,/^  }/p' "$file" | sed '1s/.*"config": *//')

echo "Registering connector '$name' at $connect_url"
status=$(curl -s -o /tmp/register-response.json -w '%{http_code}' -X PUT \
  -H 'Content-Type: application/json' --data "$config" \
  "$connect_url/connectors/$name/config")
case "$status" in
  200|201) echo "Configuration accepted (HTTP $status)" ;;
  *) echo "Registration failed (HTTP $status):" >&2; cat /tmp/register-response.json >&2; echo >&2; exit 1 ;;
esac

attempt=0
while [ "$attempt" -lt 30 ]; do
  state=$(curl -s "$connect_url/connectors/$name/status")
  connector_state=$(printf '%s' "$state" | sed -n 's/.*"connector": *{ *"state": *"\([A-Z]*\)".*/\1/p')
  task_states=$(printf '%s' "$state" | sed -n 's/.*"tasks": *\[\(.*\)\].*/\1/p' | grep -o '"state": *"[A-Z]*"' | sed 's/.*"\([A-Z]*\)"/\1/' | tr '\n' ' ')
  echo "connector=$connector_state tasks=$task_states"
  case "$task_states" in
    *FAILED*) echo "$state" >&2; exit 1 ;;
  esac
  all_running=$([ -n "$task_states" ] && echo yes || echo no)
  for task_state in $task_states; do
    [ "$task_state" = "RUNNING" ] || all_running=no
  done
  if [ "$connector_state" = "RUNNING" ] && [ "$all_running" = "yes" ]; then
    echo "Connector '$name' is running."
    exit 0
  fi
  attempt=$((attempt + 1))
  sleep 2
done

echo "Connector '$name' did not reach RUNNING in time:" >&2
echo "$state" >&2
exit 1
