#!/usr/bin/env python3
"""validate_turns.py — validate turn-record JSON against the turn-record.schema.json.

Usage:
    python3 tools/expert-atlas/tools/validate_turns.py <turn_record.json>
    python3 tools/expert-atlas/tools/validate_turns.py --dir <turns_dir>/
    python3 tools/expert-atlas/tools/validate_turns.py --dir models/qwen35b-a3b/artifacts/

Exit codes: 0 = all valid, 1 = validation errors, 2 = schema/IO error.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

SCHEMA_PATH = Path(__file__).resolve().parent.parent / "schema" / "turn-record.schema.json"

# Live fork contract (src/llama-cpp/tools/server/server-atlas.cpp, profile_json).
# The profile object MUST mirror the fork's /profile ProfileTurn keys exactly.
LIVE_PROFILE_REQUIRED = [
    "wall_s", "forwards", "prompt_tokens", "completion_tokens",
    "slot", "ts",
    "expert_disk_s", "expert_wait_s", "expert_matmul_s",
    "attention_s", "lm_head_s",
]
# Keys from the rejected schema revision — the fork never emitted these.
# Reject explicitly so a schema drift of this class fails loudly.
LIVE_PROFILE_FORBIDDEN = ["cache_hit_rate", "n_prompt_tokens", "n_gen_tokens"]
# Collector-transform keys that must NEVER appear in the record format.
# The wire /experts object (server-atlas.cpp experts_json_at) carries
# map/hits as hex STRINGS; tier_summary, array-form map, and hits_seq/
# hits_bitmap are collector-side transforms. Reject explicitly so a
# regression to the rejected shape fails loudly (consult d-1f8fb2b5cf:
# "$.experts_snapshot.map: expected array, got str" proved the old
# schema diverged from the wire).
LIVE_EXPERTS_FORBIDDEN = ["hits_seq", "hits_bitmap", "tier_summary"]


def load_schema() -> dict:
    with open(SCHEMA_PATH) as fh:
        return json.load(fh)


def _validate_type(instance, type_spec, path="$"):
    """Minimal JSON Schema type validator — avoids jsonschema dependency."""
    errors = []
    if isinstance(type_spec, list):
        # oneOf / anyOf style type union
        if not any(t == "null" and instance is None or
                   t == "object" and isinstance(instance, dict) or
                   t == "array" and isinstance(instance, list) or
                   t == "string" and isinstance(instance, str) or
                   t == "number" and isinstance(instance, (int, float)) or
                   t == "integer" and isinstance(instance, int) or
                   t == "boolean" and isinstance(instance, bool)
                   for t in type_spec):
            errors.append(f"{path}: expected one of {type_spec}, got {type(instance).__name__}")
        return errors
    if type_spec == "null":
        if instance is not None:
            errors.append(f"{path}: expected null, got {type(instance).__name__}")
    elif type_spec == "object":
        if not isinstance(instance, dict):
            errors.append(f"{path}: expected object, got {type(instance).__name__}")
    elif type_spec == "array":
        if not isinstance(instance, list):
            errors.append(f"{path}: expected array, got {type(instance).__name__}")
    elif type_spec == "string":
        if not isinstance(instance, str):
            errors.append(f"{path}: expected string, got {type(instance).__name__}")
    elif type_spec == "number":
        if not isinstance(instance, (int, float)):
            errors.append(f"{path}: expected number, got {type(instance).__name__}")
    elif type_spec == "integer":
        if not isinstance(instance, int) or isinstance(instance, bool):
            errors.append(f"{path}: expected integer, got {type(instance).__name__}")
    elif type_spec == "boolean":
        if not isinstance(instance, bool):
            errors.append(f"{path}: expected boolean, got {type(instance).__name__}")
    return errors


def validate_turn_record(record: dict, schema: dict | None = None) -> list[str]:
    """Validate a single turn record against the schema. Returns list of error strings."""
    if schema is None:
        schema = load_schema()
    errors = []

    # Required top-level fields
    for field in schema.get("required", []):
        if field not in record:
            errors.append(f"$: missing required field '{field}'")

    props = schema.get("properties", {})
    for field, subschema in props.items():
        if field not in record:
            continue
        value = record[field]
        path = f"$.{field}"

        # const check
        if "const" in subschema and value != subschema["const"]:
            errors.append(f"{path}: expected const {subschema['const']!r}, got {value!r}")

        # type check
        if "type" in subschema:
            errors.extend(_validate_type(value, subschema["type"], path))

        # enum check
        if "enum" in subschema and value not in subschema["enum"]:
            errors.append(f"{path}: value {value!r} not in enum {subschema['enum']}")

        # minimum/maximum for numbers
        if "minimum" in subschema and isinstance(value, (int, float)):
            if value < subschema["minimum"]:
                errors.append(f"{path}: {value} < minimum {subschema['minimum']}")
        if "maximum" in subschema and isinstance(value, (int, float)):
            if value > subschema["maximum"]:
                errors.append(f"{path}: {value} > maximum {subschema['maximum']}")

        # pattern for strings
        if "pattern" in subschema and isinstance(value, str):
            import re
            if not re.match(subschema["pattern"], value):
                errors.append(f"{path}: value {value!r} does not match pattern {subschema['pattern']!r}")

        # required sub-fields in objects
        if isinstance(value, dict) and "required" in subschema:
            for subfield in subschema["required"]:
                if subfield not in value:
                    errors.append(f"{path}: missing required field '{subfield}'")

        # Recurse into object properties
        if isinstance(value, dict) and "properties" in subschema:
            for subfield, subdef in subschema["properties"].items():
                if subfield not in value:
                    continue
                subpath = f"{path}.{subfield}"
                subval = value[subfield]
                if "const" in subdef and subval != subdef["const"]:
                    errors.append(f"{subpath}: expected const {subdef['const']!r}, got {subval!r}")
                if "type" in subdef:
                    errors.extend(_validate_type(subval, subdef["type"], subpath))
                if "enum" in subdef and subval not in subdef["enum"]:
                    errors.append(f"{subpath}: value {subval!r} not in enum {subdef['enum']}")
                if "minimum" in subdef and isinstance(subval, (int, float)):
                    if subval < subdef["minimum"]:
                        errors.append(f"{subpath}: {subval} < minimum {subdef['minimum']}")
                if "maximum" in subdef and isinstance(subval, (int, float)):
                    if subval > subdef["maximum"]:
                        errors.append(f"{subpath}: {subval} > maximum {subdef['maximum']}")
                if "pattern" in subdef and isinstance(subval, str):
                    import re
                    if not re.match(subdef["pattern"], subval):
                        errors.append(f"{subpath}: value {subval!r} does not match pattern {subdef['pattern']!r}")
                if isinstance(subval, dict) and "required" in subdef:
                    for subsubfield in subdef["required"]:
                        if subsubfield not in subval:
                            errors.append(f"{subpath}: missing required field '{subsubfield}'")

    # Provenance validation (resolve $ref)
    if "provenance" in record and "$defs" in schema:
        prov_schema = schema["$defs"].get("provenance", {})
        prov = record["provenance"]
        prov_path = "$.provenance"
        for field in prov_schema.get("required", []):
            if field not in prov:
                errors.append(f"{prov_path}: missing required field '{field}'")
        for field, subdef in prov_schema.get("properties", {}).items():
            if field not in prov:
                continue
            subpath = f"{prov_path}.{field}"
            subval = prov[field]
            if "type" in subdef:
                errors.extend(_validate_type(subval, subdef["type"], subpath))
            if "enum" in subdef and subval not in subdef["enum"]:
                errors.append(f"{subpath}: value {subval!r} not in enum {subdef['enum']}")
            if "pattern" in subdef and isinstance(subval, str):
                import re
                if not re.match(subdef["pattern"], subval):
                    errors.append(f"{subpath}: value {subval!r} does not match pattern {subdef['pattern']!r}")

    # Live-contract enforcement (explicit, beyond the generic schema walk):
    # 1. telemetry_enabled is REQUIRED and must be an explicit bool.
    #    The telemetry-off example carries telemetry_enabled:false — a
    #    record without this key FAILS validation.
    if "provenance" not in record or not isinstance(record.get("provenance"), dict):
        errors.append("$.provenance: missing required object 'provenance'")
    else:
        prov = record["provenance"]
        if "telemetry_enabled" not in prov:
            errors.append("$.provenance: missing required field 'telemetry_enabled' "
                          "(enum true/false — telemetry-off records must carry false explicitly)")
        elif not isinstance(prov["telemetry_enabled"], bool):
            errors.append("$.provenance.telemetry_enabled: expected boolean true/false, "
                          f"got {type(prov['telemetry_enabled']).__name__}")
    # 2. profile MUST carry every live-contract key, including the five
    #    honest-zero phase fields.
    if "profile" not in record or not isinstance(record.get("profile"), dict):
        errors.append("$.profile: missing required object 'profile'")
    else:
        prof = record["profile"]
        for key in LIVE_PROFILE_REQUIRED:
            if key not in prof:
                errors.append(f"$.profile: missing live-contract field '{key}' "
                              "(must mirror fork /profile ProfileTurn)")
        for key in LIVE_PROFILE_FORBIDDEN:
            if key in prof:
                errors.append(f"$.profile: forbidden field '{key}' "
                              "(fork never emitted this — use prompt_tokens/completion_tokens, drop cache_hit_rate)")
    # 3. Honest-state: telemetry off MUST have a null experts_snapshot
    #    (never zeros pretending to be data).
    if (isinstance(record.get("provenance"), dict)
            and record["provenance"].get("telemetry_enabled") is False
            and record.get("experts_snapshot") is not None):
        errors.append("$.experts_snapshot: must be null when telemetry_enabled is false")
    # 4. Wire-verbatim experts_snapshot: collector transforms
    #    (array-form map, hits_seq/hits_bitmap, tier_summary) are rejected
    #    explicitly; snapshot.turn_seq must echo the top-level turn_seq.
    #    (map/hits hex-string type+pattern is enforced by the generic
    #    schema walk above against the schema doc's pattern.)
    if isinstance(record.get("experts_snapshot"), dict):
        snap = record["experts_snapshot"]
        for key in LIVE_EXPERTS_FORBIDDEN:
            if key in snap:
                errors.append(f"$.experts_snapshot: forbidden field '{key}' "
                              "(collector transform — wire carries map/hits as hex strings, "
                              "see server-atlas.cpp experts_json_at)")
        if ("turn_seq" in snap and "turn_seq" in record
                and snap["turn_seq"] != record["turn_seq"]):
            errors.append(f"$.experts_snapshot.turn_seq: {snap['turn_seq']!r} != "
                          f"top-level turn_seq {record['turn_seq']!r} (wire echo)")

    return errors


def main(argv=None):
    ap = argparse.ArgumentParser(description="Validate turn-record JSON files")
    ap.add_argument("files", nargs="*", help="Turn record JSON files to validate")
    ap.add_argument("--dir", help="Directory of turn record JSON files to validate")
    args = ap.parse_args(argv)

    schema = load_schema()
    files = list(args.files or [])
    if args.dir:
        d = Path(args.dir)
        files.extend(sorted(d.glob("turn-*.json")))
        if not files:
            print(f"WARNING: no turn-*.json files found in {args.dir}")

    if not files:
        ap.print_help()
        sys.exit(2)

    all_errors = {}
    for path in files:
        try:
            with open(path) as fh:
                record = json.load(fh)
        except (json.JSONDecodeError, OSError) as exc:
            all_errors[str(path)] = [f"IO/parse error: {exc}"]
            continue
        errs = validate_turn_record(record, schema)
        if errs:
            all_errors[str(path)] = errs

    if all_errors:
        for path, errs in all_errors.items():
            print(f"FAIL {path}:")
            for e in errs:
                print(f"  - {e}")
        print(f"\n{len(all_errors)} file(s) with errors")
        sys.exit(1)
    else:
        print(f"OK — {len(files)} turn record(s) valid")
        sys.exit(0)


if __name__ == "__main__":
    main()
