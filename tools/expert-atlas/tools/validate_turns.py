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
            if "pattern" in subdef and isinstance(subval, str):
                import re
                if not re.match(subdef["pattern"], subval):
                    errors.append(f"{subpath}: value {subval!r} does not match pattern {subdef['pattern']!r}")

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
