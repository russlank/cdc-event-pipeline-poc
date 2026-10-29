"""Restarts the Debezium task after one known failure, and only after that one.

Debezium 3.6.2 (the code is unchanged in 3.7.0) fails its task after a Connect restart when the
first poll finds no new change: the END of the transaction restored from the stored offset has no
timestamp (NullPointerException in buildEndTransactionValue). A task restart streams normally
again. Every other failure is left alone, so ConnectorNotRunning still fires for it; so it does
when the known failure repeats more than MAX_RESTARTS times within WINDOW_SECONDS.

Logs name the connector, the task, and counts, never the failure trace.
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request

CONNECT_URL = os.environ.get("CONNECT_URL", "http://connect:8083")
CONNECTOR = os.environ.get("CONNECTOR", "hrcore-cdc")
INTERVAL_SECONDS = int(os.environ.get("INTERVAL_SECONDS", "10"))
MAX_RESTARTS = int(os.environ.get("MAX_RESTARTS", "3"))
WINDOW_SECONDS = int(os.environ.get("WINDOW_SECONDS", "600"))
KNOWN_FAILURE = "AbstractTransactionStructMaker.buildEndTransactionValue"


def log(*parts):
    print(time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), *parts, flush=True)


def failed_tasks():
    """Yields (task id, trace) of every failed task; nothing while Connect does not answer."""
    try:
        with urllib.request.urlopen(f"{CONNECT_URL}/connectors/{CONNECTOR}/status", timeout=5) as response:
            status = json.load(response)
    except (urllib.error.URLError, OSError, ValueError):
        return
    for task in status.get("tasks", []):
        if task.get("state") == "FAILED":
            yield task["id"], task.get("trace", "")


def restart(task_id):
    request = urllib.request.Request(f"{CONNECT_URL}/connectors/{CONNECTOR}/tasks/{task_id}/restart", method="POST")
    with urllib.request.urlopen(request, timeout=10):
        pass


def main():
    restarts = []
    reported = set()
    log(f"watching {CONNECTOR} every {INTERVAL_SECONDS} s")
    while True:
        now = time.monotonic()
        restarts = [at for at in restarts if now - at < WINDOW_SECONDS]
        failed = dict(failed_tasks())
        reported &= set(failed)
        for task_id, trace in failed.items():
            if KNOWN_FAILURE not in trace:
                if task_id not in reported:
                    log(f"task {task_id} of {CONNECTOR} failed with another error: not restarted")
                    reported.add(task_id)
            elif len(restarts) >= MAX_RESTARTS:
                if task_id not in reported:
                    log(f"task {task_id} of {CONNECTOR} failed again: {len(restarts)} restarts in {WINDOW_SECONDS} s, not restarted")
                    reported.add(task_id)
            else:
                try:
                    restart(task_id)
                except (urllib.error.URLError, OSError) as error:
                    log(f"restart of task {task_id} of {CONNECTOR} failed: {type(error).__name__}")
                    continue
                restarts.append(now)
                log(f"restarted task {task_id} of {CONNECTOR} after the known END-timestamp failure ({len(restarts)} in {WINDOW_SECONDS} s)")
        time.sleep(INTERVAL_SECONDS)


if __name__ == "__main__":
    sys.exit(main())
