"""Webhook sink for Alertmanager: prints one line per alert, so alerts show in its log.

Alerts carry only labels and annotations defined in monitoring/prometheus/alerts.yml (codes,
topic and group names, numbers), never row content.
"""

import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        try:
            payload = json.loads(self.rfile.read(length))
        except ValueError:
            self.send_response(400)
            self.end_headers()
            return
        for alert in payload.get("alerts", []):
            labels = alert.get("labels", {})
            print(
                "ALERT",
                alert.get("status", "unknown").upper(),
                labels.get("alertname", "?"),
                f"severity={labels.get('severity', '?')}",
                f"since={alert.get('startsAt', '?')}",
                f"summary={alert.get('annotations', {}).get('summary', '')!r}",
                flush=True,
            )
        self.send_response(200)
        self.end_headers()

    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"ok\n")

    def log_message(self, format, *args):
        # Request lines are noise; only the alerts above are printed.
        pass


if __name__ == "__main__":
    print("alert sink listening on :8080", flush=True)
    sys.stdout.flush()
    HTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
