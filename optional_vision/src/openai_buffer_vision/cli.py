from __future__ import annotations

import argparse
import importlib.util
import json
import shutil
import sys
from collections.abc import Sequence
from pathlib import Path

from .capture import parse_region
from .client import DaemonClient
from .pipeline import PipelineOptions, observe


def _doctor() -> int:
    checks = {
        "python": sys.version.split()[0],
        "screen_capture": importlib.util.find_spec("mss") is not None,
        "http_client": importlib.util.find_spec("httpx") is not None,
        "windows_uia": importlib.util.find_spec("pywinauto") is not None,
        "opencv": importlib.util.find_spec("cv2") is not None,
        "pytesseract": importlib.util.find_spec("pytesseract") is not None,
        "tesseract_executable": shutil.which("tesseract"),
        "rapidocr": importlib.util.find_spec("rapidocr") is not None,
        "onnxruntime": importlib.util.find_spec("onnxruntime") is not None,
    }
    print(json.dumps(checks, indent=2))
    return 0 if checks["screen_capture"] and checks["http_client"] else 1


def _observe(args: argparse.Namespace) -> int:
    region = parse_region(args.region) if args.region else None
    result = observe(
        PipelineOptions(
            monitor=args.monitor,
            region=region,
            use_uia=args.uia,
            use_cv=args.cv,
            use_ocr=args.ocr,
            ocr_engine=args.ocr_engine,
            include_containers=args.include_containers,
            max_uia_nodes=args.max_uia_nodes,
            request_id=args.request_id,
        )
    )
    payload = result.observation.to_dict()
    rendered = json.dumps(payload, indent=2, ensure_ascii=False)

    if args.output:
        output = Path(args.output).expanduser().resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(rendered + "\n", encoding="utf-8")
    else:
        print(rendered)

    if args.overlay:
        from .overlay import write_overlay

        write_overlay(
            result.frame.image,
            result.frame.viewport,
            result.observation.elements,
            Path(args.overlay).expanduser().resolve(),
        )

    if args.post:
        response = DaemonClient(args.daemon_url).post_observation(payload)
        print(json.dumps(response, indent=2))

    if result.warnings:
        for warning in result.warnings:
            print(f"warning: {warning}", file=sys.stderr)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="buffer-vision",
        description="Read-only desktop observation producer for OpenAI Buffer Daemon.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("doctor", help="Report installed perception backends.")

    observe_parser = subparsers.add_parser("observe", help="Capture one observation.")
    observe_parser.add_argument(
        "--monitor",
        type=int,
        default=1,
        help="MSS monitor index; 0 is the virtual desktop.",
    )
    observe_parser.add_argument(
        "--region", help="Physical screen region: left,top,width,height."
    )
    observe_parser.add_argument(
        "--uia", action=argparse.BooleanOptionalAction, default=True
    )
    observe_parser.add_argument(
        "--cv", action=argparse.BooleanOptionalAction, default=False
    )
    observe_parser.add_argument(
        "--ocr", action=argparse.BooleanOptionalAction, default=False
    )
    observe_parser.add_argument("--include-containers", action="store_true")
    observe_parser.add_argument(
        "--ocr-engine", choices=("tesseract", "paddle"), default="tesseract"
    )
    observe_parser.add_argument("--max-uia-nodes", type=int, default=300)
    observe_parser.add_argument("--request-id")
    observe_parser.add_argument(
        "--output", help="Write the observation JSON to this path."
    )
    observe_parser.add_argument(
        "--overlay", help="Write a labeled PNG; requires the CV extra."
    )
    observe_parser.add_argument(
        "--post", action="store_true", help="POST the observation to the daemon."
    )
    observe_parser.add_argument("--daemon-url", default="http://127.0.0.1:5087")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.command == "doctor":
        return _doctor()
    if args.command == "observe":
        return _observe(args)
    raise AssertionError(f"Unhandled command: {args.command}")
