from __future__ import annotations

import argparse
import json
import sys
from dataclasses import asdict, dataclass, replace
from pathlib import Path
from typing import Any

import numpy as np
from astropy.io import fits

from climation_checker.acceleration import argmax_position, backend_name
from climation_checker.fits_inspector import RingMetrics, analyze_rings_array


@dataclass
class ViewerAnalysis:
    source_file: str
    preview_file: str
    crop_data_file: str
    crop_origin_x: int
    crop_origin_y: int
    crop_width: int
    crop_height: int
    backend: str
    ring_metrics: dict | None
    error: str | None


@dataclass
class StretchProfile:
    name: str
    adjustable: bool


STRETCH_PROFILES: dict[str, StretchProfile] = {
    "low": StretchProfile("Low", True),
    "medium": StretchProfile("Medium", True),
    "high": StretchProfile("High", True),
    "moon": StretchProfile("Moon", False),
    "planet": StretchProfile("Planet", False),
    "max val": StretchProfile("Max Val", False),
    "range": StretchProfile("Range", False),
    "floating": StretchProfile("Floating", True),
    "manual": StretchProfile("Manual", True),
}


def build_viewer_analysis(
    source_path: Path,
    output_dir: Path,
    raw_metadata_path: Path | None = None,
) -> ViewerAnalysis:
    _emit_progress(46, "Analyze", "Loading the frame into Python memory.")
    source_name = source_path.name
    if raw_metadata_path is None:
        data = fits.getdata(source_path).astype(np.float32)
    else:
        data = load_raw_frame(source_path, raw_metadata_path)

    peak_x, peak_y = locate_primary_source(data)
    analysis_half_size = min(max(min(data.shape) // 2, 900), 1200)
    analysis_x0 = max(0, peak_x - analysis_half_size)
    analysis_y0 = max(0, peak_y - analysis_half_size)
    analysis_x1 = min(data.shape[1], peak_x + analysis_half_size)
    analysis_y1 = min(data.shape[0], peak_y + analysis_half_size)
    analysis_data = data[analysis_y0:analysis_y1, analysis_x0:analysis_x1]

    metrics: RingMetrics | None = None
    error: str | None = None
    try:
        _emit_progress(58, "Analyze", "Running donut ring detection on the source ROI.")
        local_metrics = analyze_rings_array(analysis_data, source_name, progress_callback=_emit_progress)
        metrics = replace(
            local_metrics,
            outer_center_x=local_metrics.outer_center_x + analysis_x0,
            outer_center_y=local_metrics.outer_center_y + analysis_y0,
            inner_center_x=local_metrics.inner_center_x + analysis_x0,
            inner_center_y=local_metrics.inner_center_y + analysis_y0,
        )
    except RuntimeError as exc:
        error = str(exc)
        try:
            _emit_progress(66, "Analyze", "Retrying on the full frame for maximum reliability.")
            metrics = analyze_rings_array(data, source_name, progress_callback=_emit_progress)
            error = None
        except RuntimeError as full_frame_exc:
            error = str(full_frame_exc)

    _emit_progress(80, "Analyze", "Selecting the preview crop around the source.")
    crop_origin_x = analysis_x0
    crop_origin_y = analysis_y0
    crop_width = int(analysis_x1 - analysis_x0)
    crop_height = int(analysis_y1 - analysis_y0)

    if metrics is not None:
        crop_half_size = int(max(metrics.outer_radius_px * 1.85, 180))
        center_x = int(round(metrics.outer_center_x))
        center_y = int(round(metrics.outer_center_y))
        x0 = max(0, center_x - crop_half_size)
        y0 = max(0, center_y - crop_half_size)
        x1 = min(data.shape[1], center_x + crop_half_size)
        y1 = min(data.shape[0], center_y + crop_half_size)
        preview_data = data[y0:y1, x0:x1]
        crop_origin_x = x0
        crop_origin_y = y0
        crop_width = int(x1 - x0)
        crop_height = int(y1 - y0)
    else:
        preview_data = analysis_data

    output_dir.mkdir(parents=True, exist_ok=True)
    crop_data_path = output_dir / f"{source_path.stem}-crop.float32"
    _emit_progress(88, "Analyze", "Writing the preview crop for the C# renderer.")
    save_crop_data(preview_data, crop_data_path)
    _emit_progress(92, "Analyze", "Finalizing the analysis payload.")

    return ViewerAnalysis(
        source_file=str(source_path.resolve()),
        preview_file="",
        crop_data_file=str(crop_data_path.resolve()),
        crop_origin_x=crop_origin_x,
        crop_origin_y=crop_origin_y,
        crop_width=crop_width,
        crop_height=crop_height,
        backend=backend_name(),
        ring_metrics=None if metrics is None else asdict(metrics),
        error=error,
    )


def load_raw_frame(raw_path: Path, metadata_path: Path) -> np.ndarray:
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    width = int(_metadata_value(metadata, "Width"))
    height = int(_metadata_value(metadata, "Height"))
    bit_depth = int(_metadata_value(metadata, "BitDepth", 16))
    pixel_format = str(_metadata_value(metadata, "PixelFormat", "Gray16LittleEndian"))

    if bit_depth != 16 or pixel_format.lower() != "gray16littleendian":
        raise ValueError(f"Unsupported RAW frame format: bit_depth={bit_depth}, pixel_format={pixel_format}")

    frame = np.fromfile(raw_path, dtype="<u2")
    expected = width * height
    if frame.size != expected:
        raise ValueError(f"RAW frame size mismatch for {raw_path.name}: expected {expected} pixels, got {frame.size}")

    return frame.reshape((height, width)).astype(np.float32)


def save_crop_data(data: np.ndarray, output_path: Path) -> None:
    np.asarray(data, dtype="<f4").tofile(output_path)


def locate_primary_source(data: np.ndarray) -> tuple[int, int]:
    return argmax_position(data, sigma=6.0)


def _metadata_value(metadata: dict[str, Any], key: str, default: Any | None = None) -> Any:
    for candidate in (key, key[0].lower() + key[1:]):
        if candidate in metadata:
            return metadata[candidate]
    return default


def _emit_progress(percent: int, headline: str, detail: str) -> None:
    print(f"PROGRESS|{percent}|{headline}|{detail}", file=sys.stderr, flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description="Build a stretched preview plus ring metrics for the UI.")
    parser.add_argument("--file", type=Path, help="Path to a FITS file.")
    parser.add_argument("--raw-file", type=Path, help="Path to a RAW uint16 frame.")
    parser.add_argument("--raw-metadata", type=Path, help="Path to the RAW metadata JSON.")
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path("output") / "viewer",
        help="Directory where the preview PNG will be saved.",
    )
    args = parser.parse_args()

    if args.file is None and args.raw_file is None:
        raise SystemExit("Either --file or --raw-file must be provided.")
    if args.raw_file is not None and args.raw_metadata is None:
        raise SystemExit("--raw-metadata is required when --raw-file is used.")

    source_path = args.file if args.file is not None else args.raw_file
    assert source_path is not None

    result = build_viewer_analysis(
        source_path=source_path,
        output_dir=args.output_dir,
        raw_metadata_path=args.raw_metadata,
    )
    print(json.dumps(asdict(result), indent=2))


if __name__ == "__main__":
    main()
