#!/usr/bin/env python3
"""找出还没包进 Loc.Pick 的中文字符串字面量。

只检查"该翻译却没翻译"的地方：注释与 Verse.Log 诊断日志里的中文不算问题。

下面这些文件里有**故意**保留的裸中文，已登记在 ALLOWED 里（改这些文件时请看清原因）：
  * PromptBuilder.cs       —— 提示词的中文版本常量（Zh*），英文版本在同文件的 En*；
  * LlmProviderPresets.cs  —— 服务商的中文名 LabelZh，英文名 LabelEn；
  * ActionSelector.cs      —— 关键词表（中英关键词并列，用于匹配模型输出的标题/概念）；
  * ActionSpot.cs / WhoResolver.cs —— 公开的中文帮助常量，英文分支用 *En 常量；
  * MockGateway.cs         —— 假模型靠中文提示词小节标题识别调用类型，中英文都要匹配；
  * BuiltinHandlers.cs     —— CategoryHint 的 zh 变量（英文变量 en 紧邻其右）；
  * EventRuntime.cs / DirectorLoop.cs —— 仅写进 Verse.Log 的截断后缀。

用法（仓库根目录）：python tools/check_cjk.py
退出码：0 = 没有遗漏，1 = 有遗漏。
"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, "Source", "AINarrator")
CJK = re.compile(r'[\u4e00-\u9fff]')

ALLOWED = {
    os.path.join("Director", "PromptBuilder.cs"): "中英双语提示词常量",
    os.path.join("Providers", "LlmProviderPresets.cs"): "服务商中文名 LabelZh",
    os.path.join("Actions", "ActionSelector.cs"): "中英关键词表",
    os.path.join("Actions", "ActionSpot.cs"): "中文帮助常量 + *En 常量",
    os.path.join("Actions", "WhoResolver.cs"): "中文帮助常量 + *En 常量",
    os.path.join("Providers", "MockGateway.cs"): "中英文提示词探针",
    os.path.join("Actions", "BuiltinHandlers.cs"): "CategoryHint 的 zh/en 变量",
    os.path.join("Events", "EventRuntime.cs"): "仅日志用的截断后缀",
    os.path.join("Director", "DirectorLoop.cs"): "仅日志用的截断后缀",
    os.path.join("Actions", "HandleRegistry.cs"): "Loc.IsChinese 的 zh 分支常量 + 预算 labelZh 参数",
    os.path.join("Director", "LookupProtocol.cs"): "兼容中文 LOOKUP 命令别名（'查询:'），属协议兼容",
}


def strip_comments(src):
    out, i, n = [], 0, len(src)
    while i < n:
        if src[i] == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif src[i] == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r"[^\n]", " ", src[i:j]))
            i = j
        else:
            out.append(src[i])
            i += 1
    return "".join(out)


def literals(src):
    i, n = 0, len(src)
    while i < n:
        if src[i] == '"':
            j, buf = i + 1, []
            while j < n:
                if src[j] == "\\":
                    buf.append(src[j:j + 2])
                    j += 2
                    continue
                if src[j] == '"':
                    break
                buf.append(src[j])
                j += 1
            yield (i, j + 1, "".join(buf))
            i = j + 1
        else:
            i += 1


def pick_spans(src):
    spans = []
    for match in re.finditer(r"Loc\.Pick\s*\(", src):
        depth, i = 1, match.end()
        while i < len(src) and depth > 0:
            ch = src[i]
            if ch == '"':
                j = i + 1
                while j < len(src):
                    if src[j] == "\\":
                        j += 2
                        continue
                    if src[j] == '"':
                        break
                    j += 1
                i = j + 1
                continue
            if ch in "([{":
                depth += 1
            elif ch in ")]}":
                depth -= 1
            i += 1
        spans.append((match.end(), i))
    return spans


problems, allowed_hits = [], 0
for path in glob.glob(os.path.join(SOURCE, "**", "*.cs"), recursive=True):
    if os.sep + "obj" + os.sep in path:
        continue
    rel = os.path.relpath(path, SOURCE)
    src = strip_comments(open(path, encoding="utf-8-sig").read())
    spans = pick_spans(src)
    found = []
    for start, end, value in literals(src):
        if not CJK.search(value):
            continue
        if any(a <= start < b for a, b in spans):
            continue
        line_start = src.rfind("\n", 0, start) + 1
        line_end = src.find("\n", end)
        line = src[line_start:line_end if line_end > 0 else end]
        if "Verse.Log" in line or "Log." in line:
            continue
        found.append(value[:80])
    if not found:
        continue
    if rel in ALLOWED:
        allowed_hits += len(found)
        continue
    problems.extend("%s: %r" % (rel, text) for text in found)

for item in problems:
    print(item)
print("允许保留的中文 %d 处（%d 个已说明的文件）；未翻译 %d 处"
      % (allowed_hits, len(ALLOWED), len(problems)))
if problems:
    sys.exit(1)
print("OK: 0 个问题")
