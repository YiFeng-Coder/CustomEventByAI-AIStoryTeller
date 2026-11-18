#!/usr/bin/env python3
"""Keyed 文件与代码的一致性自检。

检查项：
  1. 每个 Keyed 文件是 LanguageData 根、无重复键、无子元素；
  2. English 与 ChineseSimplified 的键集合完全一致；
  3. 每个键的 {n} 占位符在两语言间一致（否则 string.Format 会错位或抛异常）；
  4. 代码里 Loc.T("Key") 用到的键都存在；
  5. 每个 Loc.Pick 恰好两个参数，且英文参数里没有中文字符。

用法（仓库根目录）：python tools/check_loc.py
退出码：0 = 全部通过，1 = 有问题。
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CJK = re.compile(r'[\u4e00-\u9fff]')
PLACEHOLDER = re.compile(r'\{(\d+)\}')
problems = []


def load(path):
    tree = ET.parse(path)
    root = tree.getroot()
    if root.tag != "LanguageData":
        problems.append("%s: 根元素是 <%s>，应为 <LanguageData>" % (path, root.tag))
    keys = {}
    for child in root:
        if not isinstance(child.tag, str):
            continue
        if child.tag in keys:
            problems.append("%s: 重复键 %s" % (path, child.tag))
        if len(list(child)) > 0:
            problems.append("%s: <%s> 含子元素" % (path, child.tag))
        keys[child.tag] = child.text or ""
    return keys


en, zh = {}, {}
for p in glob.glob(os.path.join(ROOT, "Languages", "English", "Keyed", "*.xml")):
    en.update(load(p))
for p in glob.glob(os.path.join(ROOT, "Languages", "ChineseSimplified", "Keyed", "*.xml")):
    zh.update(load(p))

for k in sorted(set(en) - set(zh)):
    problems.append("键只在英文里: %s" % k)
for k in sorted(set(zh) - set(en)):
    problems.append("键只在中文里: %s" % k)

for k in sorted(set(en) & set(zh)):
    a = sorted(set(PLACEHOLDER.findall(en[k])))
    b = sorted(set(PLACEHOLDER.findall(zh[k])))
    if a != b:
        problems.append("占位符不一致 %s: en=%s zh=%s" % (k, a, b))
    for text, tag in ((en[k], "en"), (zh[k], "zh")):
        stripped = PLACEHOLDER.sub("", text)
        if "{" in stripped or "}" in stripped:
            problems.append("花括号未转义 %s(%s): %r" % (k, tag, text[:70]))


def read_source(path):
    with open(path, encoding="utf-8-sig") as handle:
        return handle.read()


def call_args(src, name):
    """逐个找出 name(...) 的顶层参数区间。"""
    for match in re.finditer(re.escape(name) + r"\s*\(", src):
        start = match.end()
        depth, i, arg_start, args = 1, start, start, []
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
                if depth == 0:
                    args.append((arg_start, i))
                    break
            elif ch == "," and depth == 1:
                args.append((arg_start, i))
                arg_start = i + 1
            i += 1
        yield args


sources = [p for p in glob.glob(os.path.join(ROOT, "Source", "AINarrator", "**", "*.cs"), recursive=True)
           if os.sep + "obj" + os.sep not in p]
used = set()
for path in sources:
    src = read_source(path)
    for args in call_args(src, "Loc.Pick"):
        if len(args) != 2:
            problems.append("%s: Loc.Pick 有 %d 个参数" % (path, len(args)))
            continue
        if CJK.search(src[args[1][0]:args[1][1]]):
            problems.append("%s: Loc.Pick 的英文参数里有中文: %r"
                            % (path, src[args[1][0]:args[1][1]][:60]))
    for match in re.finditer(r'Loc\.T\(\s*"((?:[^"\\]|\\.)*)"', src):
        used.add(match.group(1))

for key in sorted(used - set(en)):
    problems.append("代码用到但英文 Keyed 缺失: %s" % key)
for key in sorted(used - set(zh)):
    problems.append("代码用到但中文 Keyed 缺失: %s" % key)
for key in sorted(set(en) - used):
    print("提示：Keyed 里的 %s 目前没有代码引用" % key)

print("英文键 %d / 中文键 %d / 代码引用 %d" % (len(en), len(zh), len(used)))
if problems:
    print("\n".join(problems))
    print("FAILED: %d 个问题" % len(problems))
    sys.exit(1)
print("OK: 0 个问题")
