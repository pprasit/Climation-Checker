from __future__ import annotations

import json
import sys
import traceback
from dataclasses import asdict
from pathlib import Path
from typing import Any

from climation_checker.fits_viewer_analysis import build_viewer_analysis


def main() -> None:
    print("READY", file=sys.stderr, flush=True)

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue

        try:
            request = json.loads(line)
            command = str(request.get("command", "")).lower()
            if command == "shutdown":
                print(json.dumps({"ok": True, "shutdown": True}), flush=True)
                return
            if command != "analyze":
                raise ValueError(f"Unsupported worker command: {command}")

            source_path = Path(_required(request, "source_path"))
            output_dir = Path(_required(request, "output_dir"))
            raw_metadata = request.get("raw_metadata_path")
            metadata_path = None if raw_metadata in (None, "") else Path(str(raw_metadata))

            result = build_viewer_analysis(
                source_path=source_path,
                output_dir=output_dir,
                raw_metadata_path=metadata_path,
            )
            print(json.dumps(asdict(result), separators=(",", ":")), flush=True)
        except Exception as exc:
            print(f"WORKER_ERROR|{exc}", file=sys.stderr, flush=True)
            print(
                json.dumps(
                    {
                        "source_file": "",
                        "preview_file": "",
                        "crop_data_file": "",
                        "crop_origin_x": 0,
                        "crop_origin_y": 0,
                        "crop_width": 0,
                        "crop_height": 0,
                        "backend": "",
                        "ring_metrics": None,
                        "error": str(exc),
                    },
                    separators=(",", ":"),
                ),
                flush=True,
            )


def _required(payload: dict[str, Any], key: str) -> str:
    value = payload.get(key)
    if value in (None, ""):
        raise ValueError(f"Missing required worker field: {key}")
    return str(value)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc(file=sys.stderr)
        raise
