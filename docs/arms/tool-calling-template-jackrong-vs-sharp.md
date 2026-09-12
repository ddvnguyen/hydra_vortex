# Tool-calling template comparison: Sharp (current prod) vs Jackrong/Qwen-Tool-Calling-Template-Fix

Date: 2026-09-12
Scope: pure Jinja-rendering comparison, no GPU/model involved. Production
untouched.

## What was tested

Fetched `Jackrong/Qwen-Tool-Calling-Template-Fix`'s `chat_template.jinja` from
HuggingFace (`raw/main` succeeded on first try, saved to
`/tmp/jackrong_template.jinja`) and compared it against the template currently
wired into production, `infra/llama-baseline/templates/qwen3.8-sharp.jinja`.

Built one synthetic conversation:
1. user: "What's the weather in Paris right now?"
2. assistant: `tool_calls: [{function: {name: "get_weather", arguments: '{"location": "Paris"}'}}]` — arguments as a **JSON string**, matching the OpenAI/llama.cpp convention.
3. tool: result `'{"temperature_c": 19, "condition": "cloudy"}'`
4. user follow-up: "Thanks — now tell me a fun fact about the Eiffel Tower." (should not re-trigger the tool)

Plus a matching one-function `tools` schema for `get_weather`.

Rendered both templates with `jinja2.Environment(loader=jinja2.BaseLoader()).from_string(text).render(messages=..., tools=..., add_generation_prompt=True, bos_token="", eos_token="<|im_end|>")`.

**Stubs required:** both templates needed a `tojson` filter (`json.dumps`) and
a `raise_exception` global (both templates call it for content-shape
validation; not triggered by our well-formed input, but must exist or Jinja
raises `UndefinedError` at parse-time attribute lookup... actually only
needed because the template *references* it — added a `lambda msg: (_ for _ in ()).throw(ValueError(msg))`-style raiser as a stub). Also added a
`strftime_now` global returning a fixed string, though neither template
actually called it in this code path — added defensively per instructions.
No other missing filters/globals surfaced; both templates rendered cleanly
on the first attempt with these three stubs.

## Result: rendering is byte-identical in the tool-call section

Both templates produced this for the completed tool call (verbatim from both
outputs):

```
<|im_start|>assistant
<think>

</think>

<tool_call>
<function=get_weather>
{"location": "Paris"}</function>
</tool_call><|im_end|>
<|im_start|>user
<tool_response>
{"temperature_c": 19, "condition": "cloudy"}
</tool_response><|im_end|>
<|im_start|>user
Thanks — now tell me a fun fact about the Eiffel Tower.<|im_end|>
```

The JSON-string argument (`'{"location": "Paris"}'`) comes through as clean,
unescaped JSON in both cases — no double-escaping, no Python-repr artifacts,
no visible `\"` backslash-escaping. Same for the tool result payload.

A file-level `diff` of the two full `.jinja` sources confirms why: Jackrong's
template is not an independent design — it's the same "froggeric" Qwen3
tool-calling template lineage as Sharp (Sharp's `template_version` is
`qwen3.8-froggeric-v22.5.0`; Jackrong's is `qwen3.8-froggeric-v22.3`, an
earlier point release of the same base). The tool-call argument handling
logic is essentially unchanged between the two versions:

- Both default `tool_call_format` to `'xml'`.
- Both check `tc.arguments is string` and, if so, use the raw string
  as-is (no re-serialization, no re-escaping) inside
  `<function=...>...</function>`.
- Both use `tc.arguments | tojson` only when arguments arrive as a mapping
  (not our test case, but confirmed by reading the source — same logic on
  both sides).

The actual diffs between the two files are unrelated to tool-call argument
rendering: Sharp v22.5.0 adds the "terse" system-prompt injection, a
`suppress_tool_instructions` / `[TOOL_REQUEST]` runtime-collision guard, an
extra `message.reasoning` fallback field, and minor whitespace/newline
differences in the tool-call block separator logic (e.g. blank line before
`<tool_call>` when there's no other content, `\n` vs `\n\n` in one spot).
None of these affect how a *completed* tool call + its JSON result are
displayed back to the model.

## Recommendation

**No adoption of Jackrong's template — no real rendering difference found
that would justify it.** The premise of the task (that one of the two might
garble/obscure JSON-string tool arguments) does not hold for this pair:
they are two point releases of the same upstream template with identical
argument-passthrough logic. Jackrong's version is strictly older and is
missing the terse system-prompt and the LM-Studio/`[TOOL_REQUEST]` collision
guard that Sharp added later.

Separately, and regardless of the above: production wires exactly one
`--chat-template-file` at a time (currently Sharp, via
`infra/llama-baseline/params/130-baseline-q5ks-sharp-template.yml`'s
`extra_server_args`), and that choice was made for a different reason —
wall-clock token efficiency (see `docs/arms/chat-template-sharp-*.md`).
Swapping to Jackrong's template would mean giving up Sharp's efficiency win
for zero measurable tool-call-rendering benefit. No merge of the two was
attempted here (out of scope) — if there were a genuine feature in
Jackrong's version worth having, it would need to be hand-ported into
Sharp's already-newer base rather than switching templates wholesale.
