#!/usr/bin/env python3
"""Assemble hydra-request-detail.json (Business Text panel) embedding the
afterRender JS from _request-detail.afterRender.js. Run after editing the JS:

    python3 scripts/build-request-detail.py
"""
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
DASH_DIR = ROOT / "infra" / "grafana" / "dashboards"
JS = (DASH_DIR / "_request-detail.afterRender.js").read_text()

loki_target = {
    "datasource": {"type": "loki", "uid": "loki"},
    "expr": '{component="hydra"} |= "request_timeline"',
    "maxLines": 100,
    "refId": "A",
}

transformations = [
    {"id": "extractFields", "options": {"source": "Line", "format": "kvp"}},
    {"id": "convertFieldType", "options": {"conversions": [
        {"targetField": f, "destinationType": "number"} for f in [
            "queue_wait_ms", "prefill_ms", "save_kv_ms", "restore_kv_ms",
            "decode_ms", "total_ms", "tokens_in", "tokens_out", "kv_bytes",
        ]
    ]}},
    # NOTE: sorting is done in JS (after the timestamp fallback to
    # Loki's Time field) so the newest request is always at the top.
]

panel = {
    "id": 1,
    "type": "marcusolsson-dynamictext-panel",
    "title": "Request Timeline — Composition / Aligned + detail",
    "datasource": {"type": "loki", "uid": "loki"},
    "gridPos": {"h": 17, "w": 24, "x": 0, "y": 0},
    "targets": [loki_target],
    "transformations": transformations,
    "options": {
        "content": '<div id="hydra-tl-root"></div>',
        "defaultContent": "No request_timeline data in the selected range.",
        "editor": {"format": "auto", "language": "html"},
        "editors": ["afterRender"],
        "everyRow": False,
        "renderMode": "allRows",
        "afterRender": JS,
        "helpers": "",
        "styles": "",
        "wrap": True,
    },
}

dashboard = {
    "title": "Hydra \u2014 Request Detail",
    "uid": "hydra-request-detail",
    "version": 4,
    "timezone": "browser",
    "editable": True,
    "tags": ["hydra", "timeline", "performance"],
    "schemaVersion": 39,
    "panels": [panel],
    "refresh": "30s",
    "time": {"from": "now-1h", "to": "now"},
    "timepicker": {},
}

out = DASH_DIR / "hydra-request-detail.json"
out.write_text(json.dumps(dashboard, indent=2))
print(f"wrote {out} ({out.stat().st_size} bytes, JS {len(JS)} chars)")
