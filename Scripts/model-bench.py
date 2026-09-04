#!/usr/bin/env python3
"""WinFinger 模型自测：用一小组基准题快速判断「这个模型能不能用」。

题目按 https://egenthub.com/egenthub-perspect/2026-03-20-llm-benchmark-guide-15-metrics
里的基准家族各取一两道代表题（MMLU / C-Eval / HellaSwag / GPQA / GSM8K / ARC-AGI /
HumanEval / SciCode 风格），全部本地自动判分——不是复刻官方榜单，只是把同类能力各测一下，
外加首字延迟和输出速度，方便在换模型 / 换网关时横向比较。

用法：
    set WINFINGER_BENCH_KEY=sk-...
    python Scripts/model-bench.py --base-url https://api.example.com/v1 --model gpt-4o-mini

安全提示：HumanEval 类题目会执行模型返回的代码。执行在独立子进程里做，带 10 秒超时，
但仍然是在你本机跑陌生代码；只对你信任的模型 / 网关使用。加 --no-exec 可以跳过这类题。
"""

import argparse
import json
import math
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

TIMEOUT = 120
EXEC_TIMEOUT = 10


# ── 题库 ────────────────────────────────────────────────────────────────────

CHOICE_SUFFIX = "\n只回答选项字母，不要解释。"
NUMBER_SUFFIX = "\n最后一行只写最终答案的数字，不要单位、不要解释。"

ITEMS = [
    {
        "id": "mmlu-physics",
        "family": "MMLU · 学科知识",
        "prompt": "一个物体从静止开始自由下落 3 秒（g = 9.8 m/s²，忽略空气阻力），"
                  "下落距离最接近以下哪个？\nA. 14.7 m\nB. 29.4 m\nC. 44.1 m\nD. 88.2 m" + CHOICE_SUFFIX,
        "grade": ("choice", "C"),
    },
    {
        "id": "ceval-chinese",
        "family": "C-Eval · 中文语境",
        "prompt": "「刻舟求剑」这个成语讽刺的是下面哪一种人？\n"
                  "A. 见异思迁、朝三暮四的人\nB. 拘泥固执、不知变通的人\n"
                  "C. 贪得无厌、得寸进尺的人\nD. 弄虚作假、欺世盗名的人" + CHOICE_SUFFIX,
        "grade": ("choice", "B"),
    },
    {
        "id": "hellaswag-commonsense",
        "family": "HellaSwag · 常识推理",
        "prompt": "情景：小李把一锅水放到燃气灶上，开了大火，然后去客厅看了二十分钟电视。"
                  "他回到厨房时最可能看到什么？\n"
                  "A. 水温还和刚放上去时一样\nB. 水已经烧开，可能烧干了一部分\n"
                  "C. 水结成了冰\nD. 锅自己挪到了水槽里" + CHOICE_SUFFIX,
        "grade": ("choice", "B"),
    },
    {
        "id": "gpqa-chemistry",
        "family": "GPQA · 专家级",
        "prompt": "下列关于苯环亲电取代反应的说法，哪一个是正确的？\n"
                  "A. —NO₂ 是邻对位定位基，使苯环活化\n"
                  "B. —OCH₃ 是间位定位基，使苯环钝化\n"
                  "C. —OCH₃ 是邻对位定位基，使苯环活化\n"
                  "D. —CH₃ 是间位定位基，使苯环活化" + CHOICE_SUFFIX,
        "grade": ("choice", "C"),
    },
    {
        "id": "gsm8k-1",
        "family": "GSM8K · 应用题",
        "prompt": "小明买了 3 支笔和 2 本本子，一共花了 47 元。已知一本本子比一支笔贵 4 元。"
                  "一支笔多少钱？" + NUMBER_SUFFIX,
        "grade": ("number", 7.8),
    },
    {
        "id": "gsm8k-2",
        "family": "GSM8K · 应用题",
        "prompt": "一个水池有进水管和出水管。单开进水管 6 小时注满，单开出水管 9 小时放空。"
                  "水池空着时同时打开两个管，多少小时能注满？" + NUMBER_SUFFIX,
        "grade": ("number", 18),
    },
    {
        "id": "arc-pattern",
        "family": "ARC-AGI · 抽象规律",
        "prompt": "观察这个序列并给出第 6 项：2, 3, 5, 9, 17, ?" + NUMBER_SUFFIX,
        "grade": ("number", 33),
    },
    {
        "id": "humaneval-1",
        "family": "HumanEval · 基础编码",
        "prompt": "写一个 Python 函数 `def longest_common_prefix(strs: list[str]) -> str:`，"
                  "返回字符串列表的最长公共前缀，列表为空时返回空字符串。"
                  "只输出一个 ```python 代码块，不要解释、不要示例调用。",
        "grade": ("code", [
            "assert longest_common_prefix(['flower','flow','flight']) == 'fl'",
            "assert longest_common_prefix(['dog','racecar','car']) == ''",
            "assert longest_common_prefix([]) == ''",
            "assert longest_common_prefix(['abc']) == 'abc'",
        ]),
    },
    {
        "id": "humaneval-2",
        "family": "HumanEval · 边界处理",
        "prompt": "写一个 Python 函数 `def merge_intervals(intervals: list[list[int]]) -> list[list[int]]:`，"
                  "合并重叠区间并按起点升序返回。输入可能是空列表或乱序。"
                  "只输出一个 ```python 代码块，不要解释。",
        "grade": ("code", [
            "assert merge_intervals([[1,3],[2,6],[8,10],[15,18]]) == [[1,6],[8,10],[15,18]]",
            "assert merge_intervals([[1,4],[4,5]]) == [[1,5]]",
            "assert merge_intervals([]) == []",
            "assert merge_intervals([[5,6],[1,2]]) == [[1,2],[5,6]]",
        ]),
    },
    {
        "id": "scicode-numeric",
        "family": "SciCode · 科学计算",
        "prompt": "写一个 Python 函数 `def simpson(f, a: float, b: float, n: int) -> float:`，"
                  "用复合辛普森法在 [a, b] 上对 f 数值积分，n 为偶数区间数。"
                  "只输出一个 ```python 代码块，不要解释。",
        "grade": ("code", [
            "import math",
            "assert abs(simpson(math.sin, 0, math.pi, 100) - 2.0) < 1e-6",
            "assert abs(simpson(lambda x: x*x, 0, 3, 100) - 9.0) < 1e-6",
        ]),
    },
]


# ── 判分 ────────────────────────────────────────────────────────────────────

def grade_choice(reply: str, expected: str) -> bool:
    letters = re.findall(r"\b([A-D])\b", reply.upper())
    return bool(letters) and letters[0] == expected


def grade_number(reply: str, expected: float) -> bool:
    numbers = re.findall(r"-?\d+(?:\.\d+)?", reply.replace(",", ""))
    if not numbers:
        return False
    try:
        return abs(float(numbers[-1]) - expected) < 0.05
    except ValueError:
        return False


def extract_code(reply: str) -> str:
    # 围栏语言标签写法五花八门（py / Python / python3）：只要求"反引号 + 可选标签 + 换行"
    fenced = re.findall(r"```[^\n`]*\n(.*?)```", reply, re.S)
    return fenced[0] if fenced else reply


def grade_code(reply: str, asserts: list[str], allow_exec: bool) -> tuple[bool | None, str]:
    if not allow_exec:
        return None, "skipped (--no-exec)"  # None = 不计入通过率
    source = extract_code(reply) + "\n\n" + "\n".join(asserts) + "\nprint('OK')\n"
    with tempfile.NamedTemporaryFile("w", suffix=".py", delete=False, encoding="utf-8") as handle:
        handle.write(source)
        path = handle.name
    try:
        run = subprocess.run([sys.executable, "-I", path], capture_output=True, text=True, timeout=EXEC_TIMEOUT)
        if run.returncode == 0 and "OK" in run.stdout:
            return True, "asserts passed"
        detail = (run.stderr or run.stdout).strip().splitlines()
        return False, detail[-1] if detail else f"exit {run.returncode}"
    except subprocess.TimeoutExpired:
        return False, f"timeout > {EXEC_TIMEOUT}s"
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass


# ── 调用 ────────────────────────────────────────────────────────────────────

def ask(base_url: str, key: str, model: str, prompt: str, stream: bool) -> tuple[str, float, float]:
    """返回 (回复文本, 首字延迟秒, 总耗时秒)。stream=False 时首字延迟等于总耗时。"""
    body = json.dumps({
        "model": model,
        "stream": stream,
        "temperature": 0.2,
        "messages": [
            {"role": "system", "content": "你是一个严谨的助手，严格按用户要求的格式回答。"},
            {"role": "user", "content": prompt},
        ],
    }).encode("utf-8")
    request = urllib.request.Request(
        base_url.rstrip("/") + "/chat/completions",
        data=body,
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )

    started = time.perf_counter()
    first_token = None
    chunks: list[str] = []
    with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
        if not stream:
            payload = json.loads(response.read().decode("utf-8"))
            total = time.perf_counter() - started
            return payload["choices"][0]["message"]["content"], total, total
        for raw in response:
            line = raw.decode("utf-8").strip()
            if not line.startswith("data:"):
                continue
            data = line[5:].strip()
            if data == "[DONE]":
                break
            try:
                delta = json.loads(data)["choices"][0].get("delta", {}).get("content")
            except (json.JSONDecodeError, KeyError, IndexError):
                continue
            if not delta:
                continue
            if first_token is None:
                first_token = time.perf_counter() - started
            chunks.append(delta)
    total = time.perf_counter() - started
    return "".join(chunks), first_token if first_token is not None else total, total


def main() -> int:
    # Windows 控制台默认是 cp1252/cp936，直接 print 中文会炸，统一切到 UTF-8
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    parser = argparse.ArgumentParser(description="WinFinger 模型基准自测")
    parser.add_argument("--base-url", default=os.environ.get("WINFINGER_BENCH_URL", "https://api.openai.com/v1"))
    parser.add_argument("--model", default=os.environ.get("WINFINGER_BENCH_MODEL", "gpt-4o-mini"))
    parser.add_argument("--no-exec", action="store_true", help="不执行模型返回的代码（编码题记为跳过）")
    parser.add_argument("--only", default="", help="只跑 id 包含该子串的题")
    args = parser.parse_args()

    key = os.environ.get("WINFINGER_BENCH_KEY", "").strip()
    if not key:
        print("缺少环境变量 WINFINGER_BENCH_KEY", file=sys.stderr)
        return 2

    items = [i for i in ITEMS if args.only in i["id"]]
    print(f"模型 {args.model} @ {args.base_url} · {len(items)} 道题\n")
    print(f"{'题目':<22}{'家族':<22}{'结果':<6}{'首字 s':>8}{'总耗时 s':>10}  说明")
    print("-" * 96)

    passed = 0
    skipped = 0
    latencies: list[float] = []
    first_tokens: list[float] = []
    speeds: list[float] = []

    for index, item in enumerate(items):
        kind, expected = item["grade"]
        stream = index == 0 or kind == "code"  # 首题和编码题走流式，用来量首字延迟
        try:
            reply, ttft, total = ask(args.base_url, key, args.model, item["prompt"], stream)
        except urllib.error.HTTPError as error:
            print(f"{item['id']:<22}{item['family']:<22}{'ERR':<6}{'-':>8}{'-':>10}  HTTP {error.code}")
            continue
        except Exception as error:  # noqa: BLE001 - 基准脚本，任何失败都只记录不中断
            print(f"{item['id']:<22}{item['family']:<22}{'ERR':<6}{'-':>8}{'-':>10}  {type(error).__name__}: {error}")
            continue

        note = ""
        if kind == "choice":
            ok = grade_choice(reply, expected)
            note = f"期望 {expected}"
        elif kind == "number":
            ok = grade_number(reply, expected)
            note = f"期望 {expected}"
        else:
            ok, note = grade_code(reply, expected, allow_exec=not args.no_exec)

        if ok is None:
            skipped += 1
        else:
            passed += 1 if ok else 0
        latencies.append(total)
        if stream:
            first_tokens.append(ttft)
            if total > ttft > 0:
                speeds.append(len(reply) / (total - ttft))  # 吞吐不含首字等待
        verdict = "SKIP" if ok is None else ("PASS" if ok else "FAIL")
        print(f"{item['id']:<22}{item['family']:<22}{verdict:<6}"
              f"{ttft:>8.2f}{total:>10.2f}  {note}")

    print("-" * 96)
    scored = len(latencies) - skipped
    tail = []
    if skipped:
        tail.append(f"跳过 {skipped} 题")
    if len(latencies) < len(items):
        tail.append(f"{len(items) - len(latencies)} 题请求失败")
    print(f"通过 {passed}/{scored}" + (f"（{'，'.join(tail)}）" if tail else ""))
    if latencies:
        ordered = sorted(latencies)
        p95 = ordered[min(len(ordered) - 1, max(0, math.ceil(len(ordered) * 0.95) - 1))]
        print(f"总耗时 平均 {sum(latencies) / len(latencies):.2f}s · 中位 {ordered[len(ordered) // 2]:.2f}s · p95 {p95:.2f}s")
    if first_tokens:
        print(f"首字延迟 平均 {sum(first_tokens) / len(first_tokens):.2f}s · 最快 {min(first_tokens):.2f}s")
    if speeds:
        print(f"输出速度 平均 {sum(speeds) / len(speeds):.0f} 字符/秒")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
