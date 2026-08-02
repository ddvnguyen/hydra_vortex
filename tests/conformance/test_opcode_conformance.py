"""Three-way opcode conformance: specs <-> Hydra.Core (C#) <-> llama-engine (C++).

The wire protocol is defined in three places that nothing reconciles:

  * `specs/rpc-protocol.md`, `specs/agent-service.md`, `specs/store-service.md`
  * `src/core/Hydra.Shared/Protocol.cs`      (enum OpCode)
  * `src/llama-cpp/tools/server/server-rpc.h` (HYDRA_OP_* constants)

This has already bitten once. `tools/server/server-task.h:35-39` records that the
original 0x33-0x38 engine range (CONFIGURE..SWAP_QUANT) collided with C#'s
`OpCode.GetManifest` (0x33) and had to be renumbered to 0x40-0x46. A human caught
it. These tests catch it mechanically.

Deliberately matches on *numbers*, not names — `EngineConfigure` vs
`HYDRA_OP_CONFIGURE` never normalise cleanly across languages, and a name check
would be fragile enough that people would delete it.
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parent.parent.parent

PROTOCOL_CS = REPO_ROOT / "src/core/Hydra.Shared/Protocol.cs"
SERVER_RPC_H = REPO_ROOT / "src/llama-cpp/tools/server/server-rpc.h"

SPEC_RPC = REPO_ROOT / "specs/rpc-protocol.md"
SPEC_AGENT = REPO_ROOT / "specs/agent-service.md"
SPEC_STORE = REPO_ROOT / "specs/store-service.md"

# Opcode namespaces, by owning service. Note these are NOT clean numeric blocks:
# 0x33 GET_MANIFEST is a *Store* opcode that happens to sit inside the 0x30 range,
# which is precisely why the fork reserves 0x33-0x3F rather than using it
# (tools/server/server-task.h:35-39).
NAMESPACES = {
    "store": set(range(0x01, 0x16)) | {0x33},  # Coordinator -> Store
    "agent": set(range(0x20, 0x28)),           # Coordinator -> Agent
    "engine": {0x30, 0x31, 0x32} | set(range(0x40, 0x47)),  # Coordinator -> llama-engine
}

# The fork must not assign anything here: 0x33 is live as C#'s GetManifest, and
# the rest is buffer. The original 0x33-0x38 engine range collided and had to be
# renumbered to 0x40-0x46 — see server-task.h:35-39.
RESERVED_COLLISION_ZONE = set(range(0x33, 0x40))

# Known, accepted drift as of 2026-08-01. Each entry is a standing TODO: remove
# it from this set when the spec is updated, and the test starts enforcing it.
# Do NOT add to this set to make a failure go away without a linked issue.
KNOWN_UNDOCUMENTED = {
    0x14,  # PutMeta       — in Protocol.cs, absent from specs/store-service.md
    0x15,  # PutManifest   — in Protocol.cs, absent from specs/store-service.md
    0x26,  # SaveStateChunked    — absent from specs/agent-service.md
    0x27,  # RestoreStateChunked — absent from specs/agent-service.md
}

# 0x25 COMPLETION is documented in specs/agent-service.md but marked RETIRED,
# and Protocol.cs carries only a comment where it used to be. Expected absence.
KNOWN_RETIRED = {0x25}

MAGIC = 0x4859  # "HY", little-endian


# ── parsers ───────────────────────────────────────────────────────────────────

def parse_csharp_opcodes() -> dict[int, str]:
    """`    StateGet      = 0x30,` -> {0x30: 'StateGet'}"""
    text = PROTOCOL_CS.read_text(encoding="utf-8")
    # Only the OpCode enum — StatusCode reuses low values for a different meaning.
    # Anchor the close on a line-start `}`: doc comments in this enum contain JSON
    # examples with braces, and a non-greedy `.*?\}` truncates the enum at the
    # first one (which silently hid 0x46 from an earlier version of this parser).
    body = re.search(r"enum\s+OpCode\s*:\s*byte\s*\{(.*?)^\}", text, re.S | re.M)
    assert body, f"could not locate `enum OpCode` in {PROTOCOL_CS}"
    return {
        int(value, 16): name
        for name, value in re.findall(r"(\w+)\s*=\s*(0x[0-9A-Fa-f]{2})\s*,", body.group(1))
    }


def parse_fork_opcodes() -> dict[int, str]:
    """`static constexpr uint8_t HYDRA_OP_STATE_GET = 0x30;` -> {0x30: 'STATE_GET'}"""
    text = SERVER_RPC_H.read_text(encoding="utf-8")
    return {
        int(value, 16): name
        for name, value in re.findall(
            r"HYDRA_OP_(\w+)\s*=\s*(0x[0-9A-Fa-f]{2})\s*;", text
        )
    }


ROW = re.compile(r"^(0x[0-9A-Fa-f]{2})\s+[A-Z_]{2,}", re.M)
HEADING = re.compile(r"^#{1,6}\s+(.*)$")
SECTION_HEAD = re.compile(r"^#+\s+[A-Z_]{2,}\s*\((0x[0-9A-Fa-f]{2})\)")

# Status-code tables use the exact same row format as opcode tables
# (`0x06  NOT_IMPLEMENTED  ...`) and overlap numerically with the Store opcode
# range, so rows have to be read in the context of their heading.
STATUS_SECTION = re.compile(r"status", re.I)


def parse_spec_opcodes(path: Path) -> set[int]:
    """Handles both documented spec styles, skipping status-code tables.

    Table rows:    `0x40  CONFIGURE   Apply a common_params delta...`
    Section heads: `### PUT (0x01)`
    """
    found: set[int] = set()
    in_status_section = False

    for line in path.read_text(encoding="utf-8").splitlines():
        heading = HEADING.match(line)
        if heading:
            in_status_section = bool(STATUS_SECTION.search(heading.group(1)))
            section = SECTION_HEAD.match(line)
            if section:
                found.add(int(section.group(1), 16))
            continue

        if in_status_section:
            continue

        row = ROW.match(line)
        if row:
            found.add(int(row.group(1), 16))

    return found


def all_spec_opcodes() -> set[int]:
    return (
        parse_spec_opcodes(SPEC_RPC)
        | parse_spec_opcodes(SPEC_AGENT)
        | parse_spec_opcodes(SPEC_STORE)
    )


def namespace_of(op: int) -> str | None:
    for name, members in NAMESPACES.items():
        if op in members:
            return name
    return None


needs_fork = pytest.mark.skipif(
    not SERVER_RPC_H.exists(),
    reason="llama.cpp submodule not checked out (git submodule update --init)",
)


# ── tests ─────────────────────────────────────────────────────────────────────

def test_csharp_opcodes_parse():
    ops = parse_csharp_opcodes()
    assert len(ops) >= 20, f"parsed only {len(ops)} opcodes — parser is likely broken"


def test_every_csharp_opcode_falls_in_a_known_namespace():
    """An opcode outside every declared range is how ranges silently overlap."""
    orphans = {
        f"0x{op:02X} {name}"
        for op, name in parse_csharp_opcodes().items()
        if namespace_of(op) is None
    }
    assert not orphans, (
        f"opcodes outside every declared namespace: {sorted(orphans)}. "
        "Either place them in an existing range or add a namespace to NAMESPACES."
    )


def test_every_csharp_opcode_is_documented():
    """Guards the reverse of drift: code grows an opcode, specs never learn of it."""
    documented = all_spec_opcodes()
    missing = {
        op: name
        for op, name in parse_csharp_opcodes().items()
        if op not in documented and op not in KNOWN_UNDOCUMENTED
    }
    assert not missing, (
        "opcodes in Protocol.cs with no spec entry: "
        + ", ".join(f"0x{op:02X} {name}" for op, name in sorted(missing.items()))
        + ". Document them, or add to KNOWN_UNDOCUMENTED with a linked issue."
    )


def test_no_documented_opcode_lost_its_implementation():
    """A spec opcode with no code behind it is a promise nothing keeps."""
    implemented = set(parse_csharp_opcodes())
    if SERVER_RPC_H.exists():
        implemented |= set(parse_fork_opcodes())
    orphaned = {
        op for op in all_spec_opcodes()
        if op not in implemented
        and op not in KNOWN_RETIRED
        and namespace_of(op) is not None
    }
    assert not orphaned, (
        "opcodes documented in specs/ but implemented nowhere: "
        + ", ".join(f"0x{op:02X}" for op in sorted(orphaned))
    )


@needs_fork
def test_engine_opcode_values_agree_between_csharp_and_fork():
    """The regression that actually breaks the wire: same concept, different byte."""
    engine = NAMESPACES["engine"]
    cs = {op for op in parse_csharp_opcodes() if op in engine}
    fork = {op for op in parse_fork_opcodes() if op in engine}

    assert cs == fork, (
        "engine opcode sets disagree.\n"
        f"  only in Protocol.cs:  {sorted(f'0x{o:02X}' for o in cs - fork)}\n"
        f"  only in server-rpc.h: {sorted(f'0x{o:02X}' for o in fork - cs)}"
    )


@needs_fork
def test_fork_keeps_out_of_the_reserved_collision_zone():
    """The exact collision recorded in server-task.h:35-39 — the fork's original
    0x33-0x38 CONFIGURE..SWAP_QUANT range collided with C#'s GetManifest (0x33)
    and was renumbered to 0x40-0x46. This keeps it from recurring."""
    intruders = {
        f"0x{op:02X} HYDRA_OP_{name}"
        for op, name in parse_fork_opcodes().items()
        if op in RESERVED_COLLISION_ZONE
    }
    assert not intruders, (
        f"fork assigns opcodes inside the reserved 0x33-0x3F zone: {sorted(intruders)}. "
        "0x33 is live as C#'s OpCode.GetManifest — use the 0x40+ engine range."
    )


@needs_fork
def test_fork_does_not_claim_a_store_or_agent_opcode():
    """Broader form of the above: no fork opcode may reuse a Coordinator->Store
    or Coordinator->Agent value, whatever the numeric range."""
    reserved = {
        op: name
        for op, name in parse_csharp_opcodes().items()
        if namespace_of(op) in ("store", "agent")
    }
    collisions = {
        f"0x{op:02X}: fork HYDRA_OP_{fname} vs C# {reserved[op]}"
        for op, fname in parse_fork_opcodes().items()
        if op in reserved
    }
    assert not collisions, f"fork reuses a Store/Agent opcode: {sorted(collisions)}"


@needs_fork
def test_magic_agrees_across_implementations():
    cs = re.search(r"MAGIC\s*=\s*(0x[0-9A-Fa-f]{4})", PROTOCOL_CS.read_text(encoding="utf-8"))
    fork = re.search(
        r"HYDRA_MAGIC\s*=\s*(0x[0-9A-Fa-f]{4})", SERVER_RPC_H.read_text(encoding="utf-8")
    )
    assert cs and fork, "could not locate the magic constant in both implementations"
    assert int(cs.group(1), 16) == int(fork.group(1), 16) == MAGIC
