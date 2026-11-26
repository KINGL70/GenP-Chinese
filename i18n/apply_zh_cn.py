#!/usr/bin/env python3
"""Reusable lexical Chinese (zh-CN) localizer for GenP AutoIt (.au3) scripts.

Designed to survive upstream GenP updates:
- Parses AutoIt string literals lexically (never breaks syntax or comments).
- Protects logic/INI/registry/log calls from accidental translation.
- Writes UTF-8 with BOM (utf-8-sig), required by AutoIt/Aut2Exe for Chinese UI.
- Verifies quote, bracket, and block balance against the original source.
- Reports any newly added untranslated UI strings in future versions.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

DEFAULT_DICT_PATH = Path(__file__).resolve().parent / "zh-CN.json"

# Lines where generic `exact` string replacements should NEVER run.
NON_UI_LINE_RE = re.compile(
    r"^\s*(?:#|;)|"
    r"\b(?:IniRead\w*|IniWrite\w*|IniDelete|RegRead|RegWrite|RegDelete|"
    r"LogWrite|FileInstall|DllCall|DllStruct\w*|ObjCreate|"
    r"StringSplit|StringRegExp\w*|ProcessExists|ProcessClose)\s*\(",
    re.IGNORECASE,
)

# Functions/statements whose string arguments are user-facing UI text (for --report).
UI_REPORT_LINE_RE = re.compile(
    r"\b(?:MsgBox|GUICreate|GUICtrlCreate\w+|GUICtrlSetData|GUICtrlSetTip|"
    r"MemoWrite|InputBox|ToolTip|TrayTip|SplashTextOn|ProgressOn|ProgressSet|"
    r"FileSelectFolder|FileOpenDialog|FileSaveDialog|WinSetTitle|"
    r"_GUICtrlListView_AddColumn|_GUICtrlListView_SetColumn|"
    r"_GUICtrlStatusBar_SetText)\s*\(",
    re.IGNORECASE,
)

# Strings that are clearly technical tokens rather than human-readable UI text.
IGNORE_REPORT_STR_RE = re.compile(
    r"^(?:[\s\-\|=/:.,;!?\(\)\[\]\*\+]+|"
    r"0x[0-9a-fA-F]+|"
    r"[0-9a-fA-F\s\?]{8,}|"
    r"https?://\S+|"
    r"[\\/.][A-Za-z0-9_.\-\\]+|"
    r"[A-Za-z]:\\.+|"
    r"(?:[0-9]{1,3}\.){3}[0-9]{1,3}\s+\S+|"
    r"::1\s+\S+|"
    r"[A-Za-z0-9_.-]+\.(?:exe|dll|ps1|ini|zip|json|cs|txt|log|bak|ico|au3|plain)|"
    r"GenP-diagnostics-|"
    r"Segoe UI|Consolas|Arial|Tahoma|Calibri|Microsoft Sans Serif|"
    r"GenP(?:\s+v?[0-9.\-]+)?|"
    r"Adobe|Windows|PowerShell|mitmproxy|mitmdump|hosts|WinTrust|wintrust\.dll|"
    r"AGS|CC|CCX|UXP|ARM|x64|x86|SHA256|UPX|DNS|URL|HTTP|HTTPS|OK|N/A|---No|target|"
    r"Patched|Unpatched|Modified|Unknown|Missing|Skipped|Unsupported|Not required|starting|stopping)$",
    re.IGNORECASE,
)


def tokenize_strings(line: str) -> list[tuple[int, int, str, str]]:
    """Return [(start, end, quote, inner_text), ...] for AutoIt string literals."""
    tokens: list[tuple[int, int, str, str]] = []
    i = 0
    n = len(line)
    while i < n:
        ch = line[i]
        if ch == ";":
            break
        if ch in ('"', "'"):
            q = ch
            j = i + 1
            buf: list[str] = []
            while j < n:
                if line[j] == q:
                    if j + 1 < n and line[j + 1] == q:
                        buf.append(q)
                        j += 2
                        continue
                    tokens.append((i, j + 1, q, "".join(buf)))
                    i = j + 1
                    break
                buf.append(line[j])
                j += 1
            else:
                raise ValueError(f"Unclosed string literal starting at col {i + 1}: {line[:80]!r}")
            continue
        i += 1
    return tokens


def format_autoit_string(text: str, quote: str) -> str:
    """Escape quote character using AutoIt doubled-quote convention."""
    escaped = text.replace(quote, quote + quote)
    return f"{quote}{escaped}{quote}"


def strip_strings_and_comments(line: str) -> str:
    """Replace string literals with empty quotes and strip line comments for syntax counting."""
    out: list[str] = []
    i = 0
    n = len(line)
    while i < n:
        ch = line[i]
        if ch == ";":
            break
        if ch in ('"', "'"):
            q = ch
            j = i + 1
            while j < n:
                if line[j] == q:
                    if j + 1 < n and line[j + 1] == q:
                        j += 2
                        continue
                    out.append(q + q)
                    i = j + 1
                    break
                j += 1
            else:
                raise ValueError(f"Unclosed string literal at col {i + 1}")
            continue
        out.append(ch)
        i += 1
    return "".join(out)


def syntax_signature(lines: list[str]) -> dict[str, int]:
    """Compute structural counts (parens, brackets, blocks) outside strings/comments."""
    counts = {
        "paren_diff": 0,
        "bracket_diff": 0,
        "func": 0,
        "endfunc": 0,
        "endif": 0,
        "wend": 0,
        "next": 0,
        "endswitch": 0,
        "endselect": 0,
    }
    in_block_comment = False
    for idx, line in enumerate(lines, 1):
        st = line.strip().lower()
        if st.startswith("#cs") or st.startswith("#comments-start"):
            in_block_comment = True
            continue
        if in_block_comment:
            if st.startswith("#ce") or st.startswith("#comments-end"):
                in_block_comment = False
            continue
        try:
            code = strip_strings_and_comments(line)
        except ValueError as exc:
            raise ValueError(f"Line {idx}: {exc}") from exc
        counts["paren_diff"] += code.count("(") - code.count(")")
        counts["bracket_diff"] += code.count("[") - code.count("]")
        low = code.strip().lower()
        if re.match(r"^func\b", low):
            counts["func"] += 1
        elif re.match(r"^endfunc\b", low):
            counts["endfunc"] += 1
        elif re.match(r"^endif\b", low):
            counts["endif"] += 1
        elif re.match(r"^wend\b", low):
            counts["wend"] += 1
        elif re.match(r"^next\b", low):
            counts["next"] += 1
        elif re.match(r"^endswitch\b", low):
            counts["endswitch"] += 1
        elif re.match(r"^endselect\b", low):
            counts["endselect"] += 1
    return counts


def has_chinese(text: str) -> bool:
    return bool(re.search(r"[\u4e00-\u9fff]", text))


def apply_localization(
    lines: list[str],
    dictionary: dict,
) -> tuple[list[str], dict]:
    exact: dict[str, str] = dictionary.get("exact", {})
    contextual_raw: list[dict] = dictionary.get("contextual", [])
    line_rules: list[dict] = dictionary.get("line_rules", [])

    contextual: dict[str, list[tuple[re.Pattern[str], str]]] = {}
    for entry in contextual_raw:
        contextual.setdefault(entry["source"], []).append(
            (re.compile(entry["line_regex"]), entry["target"])
        )

    out_lines: list[str] = []
    stats = {
        "exact_hits": 0,
        "contextual_hits": 0,
        "line_rule_hits": 0,
        "modified_lines": 0,
    }

    in_block_comment = False
    for line in lines:
        st = line.strip().lower()
        if st.startswith("#cs") or st.startswith("#comments-start"):
            in_block_comment = True
            out_lines.append(line)
            continue
        if in_block_comment:
            if st.startswith("#ce") or st.startswith("#comments-end"):
                in_block_comment = False
            out_lines.append(line)
            continue

        new_line = line

        # 1. Whole-fragment line rules (for multi-part string concatenations / reordered phrasing).
        for rule in line_rules:
            if rule["find"] in new_line:
                new_line = new_line.replace(rule["find"], rule["replace"])
                stats["line_rule_hits"] += 1

        # 2. Lexical string-literal replacements.
        toks = tokenize_strings(new_line)
        if toks:
            is_non_ui = bool(NON_UI_LINE_RE.search(new_line))
            pieces: list[str] = []
            last = 0
            for start, end, quote, inner in toks:
                replacement: str | None = None

                # Contextual rules take precedence and work even on mixed lines when regex matches.
                if inner in contextual:
                    for pat, target in contextual[inner]:
                        if pat.search(new_line):
                            replacement = target
                            stats["contextual_hits"] += 1
                            break

                # Exact rules apply on safe (non-logic/non-INI/non-LogWrite) lines.
                if replacement is None and not is_non_ui and inner in exact:
                    replacement = exact[inner]
                    stats["exact_hits"] += 1

                pieces.append(new_line[last:start])
                if replacement is not None:
                    pieces.append(format_autoit_string(replacement, quote))
                else:
                    pieces.append(new_line[start:end])
                last = end
            pieces.append(new_line[last:])
            new_line = "".join(pieces)

        if new_line != line:
            stats["modified_lines"] += 1
        out_lines.append(new_line)

    return out_lines, stats


def find_untranslated_ui(lines: list[str]) -> list[tuple[int, str, str]]:
    """Scan translated lines for remaining English strings in UI function calls."""
    findings: list[tuple[int, str, str]] = []
    in_block_comment = False
    in_ui_stmt = False
    for idx, line in enumerate(lines, 1):
        st = line.strip()
        low = st.lower()
        if low.startswith("#cs") or low.startswith("#comments-start"):
            in_block_comment = True
            continue
        if in_block_comment:
            if low.startswith("#ce") or low.startswith("#comments-end"):
                in_block_comment = False
            continue
        if not st or st.startswith(";"):
            continue
        if UI_REPORT_LINE_RE.search(line):
            in_ui_stmt = True
        if in_ui_stmt and not NON_UI_LINE_RE.search(line):
            for _, _, _, inner in tokenize_strings(line):
                cleaned = inner.strip()
                if len(cleaned) < 2:
                    continue
                if has_chinese(cleaned):
                    continue
                if not re.search(r"[A-Za-z]{2,}", cleaned):
                    continue
                if IGNORE_REPORT_STR_RE.match(cleaned):
                    continue
                findings.append((idx, inner, st[:120]))
        if not st.endswith("_"):
            in_ui_stmt = False
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Apply reusable Chinese (zh-CN) UI localization to a GenP .au3 script."
    )
    parser.add_argument("target", type=Path, help="Path to GenP .au3 file")
    parser.add_argument(
        "--dict",
        type=Path,
        default=DEFAULT_DICT_PATH,
        help=f"Path to translation dictionary JSON (default: {DEFAULT_DICT_PATH.name})",
    )
    parser.add_argument("--out", type=Path, help="Optional output path (defaults to in-place)")
    parser.add_argument("--dry-run", action="store_true", help="Run without writing output file")
    parser.add_argument(
        "--report",
        action="store_true",
        help="Print remaining untranslated English UI strings after applying dictionary",
    )
    args = parser.parse_args()

    if not args.target.is_file():
        print(f"[ERROR] Target file not found: {args.target}", file=sys.stderr)
        return 1
    if not args.dict.is_file():
        print(f"[ERROR] Dictionary file not found: {args.dict}", file=sys.stderr)
        return 1

    raw_bytes = args.target.read_bytes()
    newline = "\r\n" if b"\r\n" in raw_bytes else "\n"
    original_text = raw_bytes.decode("utf-8-sig")
    orig_lines = original_text.splitlines()
    had_trailing_newline = original_text.endswith(("\n", "\r"))

    dictionary = json.loads(args.dict.read_text(encoding="utf-8"))

    orig_sig = syntax_signature(orig_lines)
    new_lines, stats = apply_localization(orig_lines, dictionary)
    new_sig = syntax_signature(new_lines)

    if orig_sig != new_sig:
        print("[ERROR] AutoIt syntax signature changed after localization!", file=sys.stderr)
        print(f"  Before: {orig_sig}", file=sys.stderr)
        print(f"  After : {new_sig}", file=sys.stderr)
        return 2

    print(
        f"[OK] {args.target}: modified {stats['modified_lines']} lines "
        f"(exact={stats['exact_hits']}, contextual={stats['contextual_hits']}, "
        f"line_rules={stats['line_rule_hits']}); syntax check PASSED."
    )

    if not args.dry_run:
        out_path = args.out or args.target
        content = newline.join(new_lines) + (newline if had_trailing_newline else "")
        out_path.write_bytes(content.encode("utf-8-sig"))
        print(f"[SAVED] {out_path} (UTF-8 with BOM)")

    untranslated = find_untranslated_ui(new_lines)
    if args.report:
        print(f"\n--- Untranslated UI candidates ({len(untranslated)}) ---")
        for ln, text, ctx in untranslated:
            print(f"  L{ln}: {text!r}  |  {ctx}")
    elif untranslated:
        print(
            f"[INFO] {len(untranslated)} untranslated UI string candidate(s) remain. "
            "Run with --report to inspect them."
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
