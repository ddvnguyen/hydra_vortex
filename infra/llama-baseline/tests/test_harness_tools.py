#!/usr/bin/env python3
"""test_harness_tools.py — hermetic tests for checkpoint-seed + smoke gate.

Runs the real shell scripts against an in-process mock llama-server (no GPU, no
llama.cpp) so the mechanics are verified on every change:

  * multiturn-growth-test.sh --checkpoint-dir saves a reproducible transcript
  * checkpoint-replay.sh rebuilds the prefix and sweeps max_tokens (one prefix,
    N calls, cached_tokens > 0 after the first)
  * --turn-prompt / --turn-prompt-file / --replay-last / --regen-filler paths
  * bad transcript format fails loudly
  * test-suite.sh --smoke passes fast on a healthy server, fails fast otherwise

Run: python3 infra/llama-baseline/tests/test_harness_tools.py
"""

import json
import os
import socket
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
BASELINE = os.path.dirname(HERE)
LIB = os.path.join(BASELINE, "lib")
sys.path.insert(0, HERE)
sys.path.insert(0, LIB)

from mock_llama_server import serve  # noqa: E402
import multiturn_common as mc  # noqa: E402

GROW = os.path.join(BASELINE, "multiturn-growth-test.sh")
REPLAY = os.path.join(BASELINE, "checkpoint-replay.sh")
SUITE = os.path.join(BASELINE, "test-suite.sh")

N_TURNS = 4
NEW_TOKENS = 400
OUTPUT_TOKENS = 16
WORDS = NEW_TOKENS * 2 // 3


def run(cmd, timeout=60):
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


class HarnessToolsTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server, cls.thread = serve(0)
        cls.port = str(cls.server.server_address[1])
        cls.state = cls.server.state

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def setUp(self):
        self.state.requests = []
        self.state.last_messages = []
        self.tmp = tempfile.TemporaryDirectory()
        self.ckpt = os.path.join(self.tmp.name, "ckpt")
        os.makedirs(self.ckpt, exist_ok=True)

    def tearDown(self):
        self.tmp.cleanup()

    def grow_with_checkpoint(self):
        r = run(["bash", GROW, self.port, "1", str(N_TURNS),
                 str(NEW_TOKENS), str(OUTPUT_TOKENS),
                 "--checkpoint-dir", self.ckpt])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("summary:", r.stdout)
        path = os.path.join(self.ckpt, "session1.transcript.json")
        self.assertTrue(os.path.exists(path), "checkpoint not written")
        with open(path) as f:
            t = json.load(f)
        return path, t

    # -- growth + checkpoint ------------------------------------------------
    def test_growth_saves_reproducible_transcript(self):
        path, t = self.grow_with_checkpoint()
        self.assertEqual(t["format"], mc.TRANSCRIPT_FORMAT)
        self.assertEqual(len(t["turns"]), N_TURNS)
        self.assertEqual(t["config"]["words_per_turn"], WORDS)
        for i, rec in enumerate(t["turns"], start=1):
            self.assertEqual(rec["turn"], i)
            self.assertEqual(rec["user_content"], mc.gen_content(i, WORDS))
            self.assertTrue(rec["assistant_content"].startswith("MOCK content"))
            self.assertGreater(rec["prompt_tokens"], 0)

    # -- replay sweep -------------------------------------------------------
    def test_replay_sweep_reuses_one_prefix(self):
        path, t = self.grow_with_checkpoint()
        # Cold server: first replay call prefills, the rest hit the prefix cache.
        self.state.requests = []
        self.state.last_messages = []
        results = os.path.join(self.tmp.name, "results.json")
        r = run(["bash", REPLAY, self.port, path,
                 "--sweep-max-tokens", "16,32,64",
                 "--save-results", results])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(len(self.state.requests), 3)
        budgets = [q["max_tokens"] for q in self.state.requests]
        self.assertEqual(budgets, [16, 32, 64])
        # Every call sends the exact same messages (prefix + target).
        first = self.state.requests[0]["messages"]
        for q in self.state.requests[1:]:
            self.assertEqual(q["messages"], first)
        # Prefix-cache reuse is visible: first call cold, later calls warm.
        self.assertEqual(self.state.requests[0]["cached_tokens"], 0)
        self.assertGreater(self.state.requests[1]["cached_tokens"], 0)
        self.assertGreater(self.state.requests[2]["cached_tokens"], 0)
        with open(results) as f:
            saved = json.load(f)
        self.assertEqual(len(saved["trials"]), 3)
        self.assertEqual(saved["config"]["budgets"], [16, 32, 64])

    def test_replay_turn_prompt_file_appends_new_final_turn(self):
        path, t = self.grow_with_checkpoint()
        prompt_file = os.path.join(self.tmp.name, "verify.txt")
        with open(prompt_file, "w") as f:
            f.write("What is SECRET_NUMBER?")
        self.state.requests = []
        r = run(["bash", REPLAY, self.port, path,
                 "--turn-prompt-file", prompt_file, "--max-tokens", "32"])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(len(self.state.requests), 1)
        msgs = self.state.requests[0]["messages"]
        # system + 4*2 history + new final user turn
        self.assertEqual(len(msgs), 1 + 2 * N_TURNS + 1)
        self.assertEqual(msgs[-1], {"role": "user", "content": "What is SECRET_NUMBER?"})
        self.assertEqual(msgs[0]["role"], "system")

    def test_replay_last_two_turns_sequential(self):
        path, t = self.grow_with_checkpoint()
        self.state.requests = []
        r = run(["bash", REPLAY, self.port, path, "--replay-last", "2"])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(len(self.state.requests), 2)
        first, second = self.state.requests
        self.assertEqual(len(second["messages"]), len(first["messages"]) + 2)
        self.assertEqual(first["messages"][-1]["content"],
                         mc.gen_content(N_TURNS - 1, WORDS))
        self.assertEqual(second["messages"][-1]["content"],
                         mc.gen_content(N_TURNS, WORDS))

    def test_regen_filler_ignores_stored_user_content(self):
        path, t = self.grow_with_checkpoint()
        with open(path) as f:
            t = json.load(f)
        t["turns"][0]["user_content"] = "TAMPERED"
        with open(path, "w") as f:
            json.dump(t, f)
        self.state.requests = []
        r = run(["bash", REPLAY, self.port, path, "--prefix-turns", "1",
                 "--max-tokens", "8"])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.state.requests[0]["messages"][1]["content"], "TAMPERED")
        self.state.requests = []
        r = run(["bash", REPLAY, self.port, path, "--prefix-turns", "1",
                 "--max-tokens", "8", "--regen-filler"])
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.state.requests[0]["messages"][1]["content"],
                         mc.gen_content(1, WORDS))

    def test_bad_transcript_format_fails(self):
        bad = os.path.join(self.tmp.name, "bad.json")
        with open(bad, "w") as f:
            json.dump({"format": "bogus/v0", "turns": [{"turn": 1,
                       "user_content": "x", "assistant_content": "y"}]}, f)
        r = run(["bash", REPLAY, self.port, bad])
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("unsupported transcript format", r.stderr + r.stdout)

    # -- smoke gate ---------------------------------------------------------
    def test_smoke_gate_passes_fast(self):
        import time
        t0 = time.time()
        r = run(["bash", SUITE, "--smoke", self.port])
        elapsed = time.time() - t0
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("SMOKE GATE PASSED", r.stdout)
        self.assertLess(elapsed, 10.0, "smoke gate should be fast")

    def test_smoke_gate_fails_fast_on_dead_port(self):
        import time
        dead = free_port()
        t0 = time.time()
        r = run(["bash", SUITE, "--smoke", str(dead)], timeout=40)
        elapsed = time.time() - t0
        self.assertEqual(r.returncode, 1)
        self.assertIn("SMOKE GATE FAILED", r.stdout + r.stderr)
        self.assertLess(elapsed, 20.0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
