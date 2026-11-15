#!/usr/bin/env python3
"""Apply the macOS-only "Starting…" state to a copy of the shared panel.

Windows' status payload has no `running` field, so this patch is intentionally
kept out of wwwroot/index.html and applied only to the macOS resource copy.
Idempotent; fails loudly if the upstream panel changed shape.
"""
import pathlib
import sys

if len(sys.argv) != 2:
    print("usage: patch-panel.py <index.html>", file=sys.stderr)
    sys.exit(2)

path = pathlib.Path(sys.argv[1])
text = path.read_text(encoding="utf-8")

dict_anchor = "    '正在连接…': 'Connecting…',\n"
dict_line = "    '启动中…': 'Starting…',\n"
if dict_line not in text:
    if dict_anchor not in text:
        raise SystemExit("patch-panel: i18n anchor not found")
    text = text.replace(dict_anchor, dict_anchor + dict_line, 1)

run_anchor = "    const label = state.paused ? '已暂停 · 静音' : '运行中';\n"
run_replacement = (
    "    const ready = data.running !== false;\n"
    "    const label = !ready ? '启动中…' : (state.paused ? '已暂停 · 静音' : '运行中');\n"
)
if "const ready = data.running !== false;" not in text:
    if run_anchor not in text:
        raise SystemExit("patch-panel: updateRun anchor not found")
    text = text.replace(run_anchor, run_replacement, 1)

path.write_text(text, encoding="utf-8")
print(f"patched {path}")
