#!/usr/bin/env python3
import argparse
import math
import re
import statistics
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Tuple

SEARCH_TIMING_RE = re.compile(
    r"\[search-timing\].*?correlationId=(?P<corr>[0-9a-fA-F]+).*?queryLength=(?P<qlen>\d+).*?path=(?P<path>\S+).*?fastPathMs=(?P<fast>\d+).*?totalMs=(?P<total>\d+)",
)

NATIVE_TEMP_RE = re.compile(
    r"\[TEMP_TIMING_REMOVE\]\s+wasmInvoke\s+op=(?P<op>\w+).*?totalMs=(?P<total>\d+).*?detail=(?P<detail>.*)$",
)

KV_RE = re.compile(r"\b(?P<key>[a-zA-Z][a-zA-Z0-9_]*)=(?P<value>\d+)")

OP_TAKES_RE = re.compile(
    r"\b(?P<op>search|get-chapters|get-pages|get-page)\s+took\s+(?P<ms>\d+)ms\b"
)


def percentile(values: List[int], p: float) -> int:
    if not values:
        return 0
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, math.ceil(p * len(ordered)) - 1))
    return ordered[index]


def summarize(values: List[int]) -> Dict[str, float]:
    if not values:
        return {"count": 0, "avg": 0.0, "p50": 0.0, "p95": 0.0, "min": 0.0, "max": 0.0}
    return {
        "count": len(values),
        "avg": statistics.mean(values),
        "p50": float(percentile(values, 0.50)),
        "p95": float(percentile(values, 0.95)),
        "min": float(min(values)),
        "max": float(max(values)),
    }


@dataclass
class SearchRecord:
    correlation_id: str
    query_length: int
    path: str
    host_total_ms: int
    fast_path_ms: int


@dataclass
class NativeRecord:
    op: str
    native_total_ms: int
    run_ms: Optional[int]
    queue_ms: Optional[int]
    worker_ms: Optional[int]


@dataclass
class ParsedLog:
    label: str
    search_records: List[SearchRecord]
    native_search_records: List[NativeRecord]
    op_timings: Dict[str, List[int]]


def parse_log(label: str, path: Path) -> ParsedLog:
    search_by_correlation: Dict[str, SearchRecord] = {}
    native_search_records: List[NativeRecord] = []
    op_timings: Dict[str, List[int]] = {
        "search": [],
        "get-chapters": [],
        "get-pages": [],
        "get-page": [],
    }

    with path.open("r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            search_match = SEARCH_TIMING_RE.search(line)
            if search_match:
                correlation_id = search_match.group("corr")
                if correlation_id not in search_by_correlation:
                    search_by_correlation[correlation_id] = SearchRecord(
                        correlation_id=correlation_id,
                        query_length=int(search_match.group("qlen")),
                        path=search_match.group("path"),
                        host_total_ms=int(search_match.group("total")),
                        fast_path_ms=int(search_match.group("fast")),
                    )
                continue

            temp_match = NATIVE_TEMP_RE.search(line)
            if temp_match and temp_match.group("op").lower() == "search":
                detail = temp_match.group("detail")
                detail_kvs = {m.group("key"): int(m.group("value")) for m in KV_RE.finditer(detail)}
                native_search_records.append(
                    NativeRecord(
                        op="search",
                        native_total_ms=int(temp_match.group("total")),
                        run_ms=detail_kvs.get("runMs"),
                        queue_ms=detail_kvs.get("workerQueueMs"),
                        worker_ms=detail_kvs.get("workerMs"),
                    )
                )
                continue

            op_match = OP_TAKES_RE.search(line)
            if op_match:
                op = op_match.group("op")
                op_timings.setdefault(op, []).append(int(op_match.group("ms")))

    return ParsedLog(
        label=label,
        search_records=list(search_by_correlation.values()),
        native_search_records=native_search_records,
        op_timings=op_timings,
    )


def format_summary(name: str, stats: Dict[str, float]) -> str:
    if stats["count"] == 0:
        return f"{name}: n=0"
    return (
        f"{name}: n={int(stats['count'])} avg={stats['avg']:.1f}ms "
        f"p50={int(stats['p50'])}ms p95={int(stats['p95'])}ms "
        f"min={int(stats['min'])}ms max={int(stats['max'])}ms"
    )


def report(parsed: ParsedLog) -> None:
    host_search = [r.host_total_ms for r in parsed.search_records]
    fast_search = [r.fast_path_ms for r in parsed.search_records]
    native_total = [r.native_total_ms for r in parsed.native_search_records]
    native_run = [r.run_ms for r in parsed.native_search_records if r.run_ms is not None]
    native_queue = [r.queue_ms for r in parsed.native_search_records if r.queue_ms is not None]

    paired_count = min(len(host_search), len(native_total))
    host_overhead = [host_search[i] - native_total[i] for i in range(paired_count)]

    print(f"\n=== {parsed.label} ===")
    print(format_summary("search.host.total", summarize(host_search)))
    print(format_summary("search.fastPath", summarize(fast_search)))
    print(format_summary("search.native.total", summarize(native_total)))
    print(format_summary("search.native.run", summarize(native_run)))
    print(format_summary("search.native.queue", summarize(native_queue)))
    print(format_summary("search.host.minus.native", summarize(host_overhead)))

    for op in ("get-chapters", "get-pages", "get-page"):
        print(format_summary(op, summarize(parsed.op_timings.get(op, []))))

    paths = sorted({r.path for r in parsed.search_records})
    if paths:
        print(f"search.paths={','.join(paths)}")


def compare(logs: List[ParsedLog]) -> None:
    if len(logs) < 2:
        return

    print("\n=== Cross-Platform Comparison (search.host.total avg/p95) ===")
    for log in logs:
        host = [r.host_total_ms for r in log.search_records]
        s = summarize(host)
        if s["count"] == 0:
            print(f"{log.label}: n=0")
        else:
            print(f"{log.label}: avg={s['avg']:.1f}ms p95={int(s['p95'])}ms n={int(s['count'])}")


def parse_input_specs(specs: List[str]) -> List[Tuple[str, Path]]:
    result: List[Tuple[str, Path]] = []
    for spec in specs:
        if "=" not in spec:
            raise SystemExit(
                f"Invalid --log spec '{spec}'. Use --log label=/absolute/or/relative/path.log"
            )
        label, path = spec.split("=", 1)
        label = label.strip()
        path_obj = Path(path.strip()).expanduser().resolve()
        if not label:
            raise SystemExit(f"Missing label in --log spec '{spec}'")
        if not path_obj.exists():
            raise SystemExit(f"Log file not found: {path_obj}")
        result.append((label, path_obj))
    return result


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Analyze EMMA native timing logs and compare platforms.",
    )
    parser.add_argument(
        "--log",
        action="append",
        required=True,
        help="Log input as label=path (repeat for multiple platforms)",
    )

    args = parser.parse_args()
    specs = parse_input_specs(args.log)
    parsed_logs = [parse_log(label, path) for label, path in specs]

    for parsed in parsed_logs:
        report(parsed)

    compare(parsed_logs)


if __name__ == "__main__":
    main()
