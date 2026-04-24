from __future__ import annotations

import argparse
import json
import sys
import warnings
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Callable

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import cv2
import matplotlib.pyplot as plt
import numpy as np
from astropy.io import fits
from astropy.stats import sigma_clipped_stats
from scipy.ndimage import binary_fill_holes
from skimage.measure import label, perimeter_crofton, regionprops
from skimage.morphology import closing, disk

from climation_checker.acceleration import gaussian_filter_accelerated


@dataclass
class FitsSummary:
    filename: str
    shape: tuple[int, ...]
    dtype: str
    min_value: float
    max_value: float
    mean_value: float
    std_value: float
    percentile_99_5: float
    exposure_seconds: float | None
    observation_time: str | None
    instrument: str | None
    peak_x: int
    peak_y: int
    peak_value: float


@dataclass
class RingMetrics:
    filename: str
    outer_center_x: float
    outer_center_y: float
    inner_center_x: float
    inner_center_y: float
    center_offset_px: float
    normalized_offset: float
    outer_radius_px: float
    inner_radius_px: float
    outer_circularity: float
    inner_circularity: float
    outer_area_px: float
    inner_area_px: float
    threshold_fraction: float
    thickness_uniformity: float
    brightness_balance: float
    brightest_quadrant: str
    outer_ellipse_ratio: float
    inner_ellipse_ratio: float
    detection_confidence: float
    confidence_label: str


@dataclass
class RingAnalysisResult:
    metrics: RingMetrics | None
    error: str | None = None


def read_fits_summary(path: Path) -> FitsSummary:
    with fits.open(path) as hdul:
        data = np.asarray(hdul[0].data)
        header = hdul[0].header

    if data.ndim != 2:
        raise ValueError(f"Expected a 2D image in {path.name}, got shape {data.shape!r}")

    finite = data[np.isfinite(data)]
    peak_y, peak_x = np.unravel_index(np.argmax(data), data.shape)

    return FitsSummary(
        filename=path.name,
        shape=tuple(int(v) for v in data.shape),
        dtype=str(data.dtype),
        min_value=float(np.min(finite)),
        max_value=float(np.max(finite)),
        mean_value=float(np.mean(finite)),
        std_value=float(np.std(finite)),
        percentile_99_5=float(np.percentile(finite, 99.5)),
        exposure_seconds=_as_float(header.get("EXPTIME")),
        observation_time=_as_str(header.get("DATE-OBS")),
        instrument=_as_str(header.get("INSTRUME")),
        peak_x=int(peak_x),
        peak_y=int(peak_y),
        peak_value=float(data[peak_y, peak_x]),
    )


def create_preview(path: Path, output_path: Path) -> None:
    with fits.open(path) as hdul:
        data = np.asarray(hdul[0].data, dtype=np.float32)

    stretched = _normalize_for_preview(data)

    fig, ax = plt.subplots(figsize=(8, 8), dpi=150)
    ax.imshow(stretched, cmap="gray", origin="lower")
    ax.set_title(path.name)
    ax.set_xlabel("X")
    ax.set_ylabel("Y")
    fig.tight_layout()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(output_path)
    plt.close(fig)


def analyze_rings(path: Path) -> RingMetrics:
    data = fits.getdata(path).astype(np.float32)
    return analyze_rings_array(data, path.name)


def analyze_rings_array(
    data: np.ndarray,
    source_name: str,
    progress_callback: Callable[[int, str, str], None] | None = None,
) -> RingMetrics:
    best_candidate = None
    smooth_raw = gaussian_filter_accelerated(np.asarray(data, dtype=np.float32), sigma=6.0)

    if progress_callback is not None:
        progress_callback(60, "Analyze", "Running the Telescope-Donut ellipse pairing.")
    fast_candidate = _find_fast_candidate_via_ellipses(data=np.asarray(data, dtype=np.float32), smooth=smooth_raw, source_name=source_name)
    if fast_candidate is not None:
        best_candidate = fast_candidate
    else:
        if progress_callback is not None:
            progress_callback(64, "Analyze", "Falling back to the contour/mask detector.")
        smooth_preprocessed = gaussian_filter_accelerated(_preprocess_for_ring_detection(data), sigma=6.0)

        candidates = [
            _find_best_ring_candidate(smooth_raw, source_name),
            _find_best_ring_candidate(smooth_preprocessed, source_name),
        ]
        ranked_candidates = [item for item in candidates if item is not None]
        best_candidate = max(ranked_candidates, key=lambda item: item[0], default=None)

    if best_candidate is None:
        raise RuntimeError(f"Could not find a donut-like ring in {source_name}")

    if isinstance(best_candidate, tuple):
        _, best_candidate = best_candidate

    refined_inner: tuple[float, float, float, float] | None = None
    should_run_expensive_refinement = (
        best_candidate.normalized_offset > 0.08 or
        best_candidate.outer_circularity < 0.35 or
        best_candidate.thickness_uniformity < 0.82 or
        best_candidate.brightness_balance < 0.95
    )

    if should_run_expensive_refinement:
        if progress_callback is not None:
            progress_callback(68, "Analyze", "Refining the inner ring center.")
        fixed_center_refined = _refine_circle_from_fixed_center(
            data=smooth_raw,
            initial_center_x=best_candidate.outer_center_x,
            initial_center_y=best_candidate.outer_center_y,
            expected_radius=best_candidate.inner_radius_px,
            search_inner_radius=max(best_candidate.inner_radius_px * 0.45, 8.0),
            search_outer_radius=min(best_candidate.inner_radius_px * 1.95, best_candidate.outer_radius_px * 0.72),
            edge_polarity="rising",
        )
        should_run_robust_search = (
            best_candidate.normalized_offset > 0.22 and
            best_candidate.thickness_uniformity < 0.80 and
            best_candidate.brightness_balance < 0.95
        )
        searched_center_refined = None
        if should_run_robust_search:
            if progress_callback is not None:
                progress_callback(72, "Analyze", "Running the robust inner-center search.")
            searched_center_refined = _search_inner_circle_from_radial_edges(
                data=smooth_raw,
                initial_center_x=best_candidate.outer_center_x,
                initial_center_y=best_candidate.outer_center_y,
                expected_radius=best_candidate.inner_radius_px,
                search_inner_radius=max(best_candidate.inner_radius_px * 0.45, 8.0),
                search_outer_radius=min(best_candidate.inner_radius_px * 1.95, best_candidate.outer_radius_px * 0.72),
                edge_polarity="rising",
            )
        elif progress_callback is not None:
            progress_callback(72, "Analyze", "The baseline refinement is already stable enough.")
        refined_inner = _choose_best_inner_refinement(
            fixed_center_refined=fixed_center_refined,
            searched_center_refined=searched_center_refined,
            expected_radius=best_candidate.inner_radius_px,
        )
    elif progress_callback is not None:
        progress_callback(72, "Analyze", "The frame already looks stable. Skipping heavy refinement.")

    if refined_inner is not None:
        refined_center_x, refined_center_y, refined_inner_radius, refinement_confidence = refined_inner
        center_offset = float(
            np.hypot(
                best_candidate.outer_center_x - refined_center_x,
                best_candidate.outer_center_y - refined_center_y,
            )
        )
        detection_confidence = _compute_detection_confidence(
            normalized_offset=float(center_offset / max(best_candidate.outer_radius_px, 1e-6)),
            outer_circularity=best_candidate.outer_circularity,
            inner_circularity=best_candidate.inner_circularity,
            thickness_uniformity=best_candidate.thickness_uniformity,
            brightness_balance=best_candidate.brightness_balance,
            outer_ellipse_ratio=best_candidate.outer_ellipse_ratio,
            inner_ellipse_ratio=best_candidate.inner_ellipse_ratio,
            refinement_confidence=refinement_confidence,
        )
        best_candidate = RingMetrics(
            filename=best_candidate.filename,
            outer_center_x=best_candidate.outer_center_x,
            outer_center_y=best_candidate.outer_center_y,
            inner_center_x=float(refined_center_x),
            inner_center_y=float(refined_center_y),
            center_offset_px=center_offset,
            normalized_offset=float(center_offset / max(best_candidate.outer_radius_px, 1e-6)),
            outer_radius_px=best_candidate.outer_radius_px,
            inner_radius_px=float(refined_inner_radius),
            outer_circularity=best_candidate.outer_circularity,
            inner_circularity=best_candidate.inner_circularity,
            outer_area_px=best_candidate.outer_area_px,
            inner_area_px=best_candidate.inner_area_px,
            threshold_fraction=best_candidate.threshold_fraction,
            thickness_uniformity=best_candidate.thickness_uniformity,
            brightness_balance=best_candidate.brightness_balance,
            brightest_quadrant=best_candidate.brightest_quadrant,
            outer_ellipse_ratio=best_candidate.outer_ellipse_ratio,
            inner_ellipse_ratio=best_candidate.inner_ellipse_ratio,
            detection_confidence=float(detection_confidence),
            confidence_label=_confidence_label(detection_confidence),
        )
    else:
        base_refinement_confidence = 0.92 if not should_run_expensive_refinement else 0.45
        detection_confidence = _compute_detection_confidence(
            normalized_offset=best_candidate.normalized_offset,
            outer_circularity=best_candidate.outer_circularity,
            inner_circularity=best_candidate.inner_circularity,
            thickness_uniformity=best_candidate.thickness_uniformity,
            brightness_balance=best_candidate.brightness_balance,
            outer_ellipse_ratio=best_candidate.outer_ellipse_ratio,
            inner_ellipse_ratio=best_candidate.inner_ellipse_ratio,
            refinement_confidence=base_refinement_confidence,
        )
        best_candidate = RingMetrics(
            filename=best_candidate.filename,
            outer_center_x=best_candidate.outer_center_x,
            outer_center_y=best_candidate.outer_center_y,
            inner_center_x=best_candidate.inner_center_x,
            inner_center_y=best_candidate.inner_center_y,
            center_offset_px=best_candidate.center_offset_px,
            normalized_offset=best_candidate.normalized_offset,
            outer_radius_px=best_candidate.outer_radius_px,
            inner_radius_px=best_candidate.inner_radius_px,
            outer_circularity=best_candidate.outer_circularity,
            inner_circularity=best_candidate.inner_circularity,
            outer_area_px=best_candidate.outer_area_px,
            inner_area_px=best_candidate.inner_area_px,
            threshold_fraction=best_candidate.threshold_fraction,
            thickness_uniformity=best_candidate.thickness_uniformity,
            brightness_balance=best_candidate.brightness_balance,
            brightest_quadrant=best_candidate.brightest_quadrant,
            outer_ellipse_ratio=best_candidate.outer_ellipse_ratio,
            inner_ellipse_ratio=best_candidate.inner_ellipse_ratio,
            detection_confidence=float(detection_confidence),
            confidence_label=_confidence_label(detection_confidence),
        )

    if progress_callback is not None:
        progress_callback(76, "Analyze", "Ring metrics are ready.")
    return best_candidate


def create_ring_overlay(path: Path, output_path: Path) -> RingMetrics:
    metrics = analyze_rings(path)
    data = fits.getdata(path).astype(np.float32)
    crop_half_size = int(max(metrics.outer_radius_px * 1.8, 180))
    cx = int(round(metrics.outer_center_x))
    cy = int(round(metrics.outer_center_y))

    y0 = max(0, cy - crop_half_size)
    y1 = min(data.shape[0], cy + crop_half_size)
    x0 = max(0, cx - crop_half_size)
    x1 = min(data.shape[1], cx + crop_half_size)
    crop = data[y0:y1, x0:x1]
    display = _normalize_for_preview(crop)

    fig, ax = plt.subplots(figsize=(6, 6), dpi=160)
    ax.imshow(display, cmap="gray", origin="lower")
    ax.set_title(path.name)
    ax.set_xlabel("X")
    ax.set_ylabel("Y")

    outer_circle = plt.Circle(
        (metrics.outer_center_x - x0, metrics.outer_center_y - y0),
        metrics.outer_radius_px,
        color="cyan",
        fill=False,
        linewidth=1.8,
    )
    inner_circle = plt.Circle(
        (metrics.inner_center_x - x0, metrics.inner_center_y - y0),
        metrics.inner_radius_px,
        color="magenta",
        fill=False,
        linewidth=1.8,
    )
    ax.add_patch(outer_circle)
    ax.add_patch(inner_circle)
    ax.scatter(
        [metrics.outer_center_x - x0, metrics.inner_center_x - x0],
        [metrics.outer_center_y - y0, metrics.inner_center_y - y0],
        c=["cyan", "magenta"],
        s=28,
    )
    ax.text(
        0.02,
        0.02,
        f"offset={metrics.center_offset_px:.2f}px\n"
        f"outer circ={metrics.outer_circularity:.3f}\n"
        f"inner circ={metrics.inner_circularity:.3f}",
        transform=ax.transAxes,
        color="white",
        fontsize=8,
        bbox={"facecolor": "black", "alpha": 0.6, "pad": 4},
    )
    fig.tight_layout()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(output_path)
    plt.close(fig)
    return metrics


def inspect_directory(image_dir: Path, preview_dir: Path | None, write_previews: bool) -> list[FitsSummary]:
    summaries: list[FitsSummary] = []

    for path in sorted(image_dir.glob("*.fit")):
        summary = read_fits_summary(path)
        summaries.append(summary)
        if write_previews and preview_dir is not None:
            create_preview(path, preview_dir / f"{path.stem}.png")

    return summaries


def main() -> None:
    warnings.filterwarnings("ignore", category=FutureWarning)
    parser = argparse.ArgumentParser(description="Inspect FITS files for collimation analysis experiments.")
    parser.add_argument(
        "--image-dir",
        type=Path,
        default=Path("Image"),
        help="Directory containing FITS files.",
    )
    parser.add_argument(
        "--preview-dir",
        type=Path,
        default=Path("output") / "previews",
        help="Directory where preview PNG files will be saved.",
    )
    parser.add_argument(
        "--skip-previews",
        action="store_true",
        help="Read FITS files without writing PNG previews.",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Print the summaries as JSON.",
    )
    parser.add_argument(
        "--analyze-rings",
        action="store_true",
        help="Compute concentricity and circularity metrics for donut-like defocused stars.",
    )
    parser.add_argument(
        "--ring-dir",
        type=Path,
        default=Path("output") / "rings",
        help="Directory where donut geometry overlays will be saved.",
    )
    args = parser.parse_args()

    if args.analyze_rings:
        analysis_results: list[RingAnalysisResult] = []
        for path in sorted(args.image_dir.glob("*.fit")):
            try:
                metrics = create_ring_overlay(path, args.ring_dir / f"{path.stem}-rings.png")
                analysis_results.append(RingAnalysisResult(metrics=metrics))
            except RuntimeError as exc:
                analysis_results.append(RingAnalysisResult(metrics=None, error=str(exc)))

        if args.json:
            print(json.dumps([asdict(item) for item in analysis_results], indent=2))
            return

        for result in analysis_results:
            if result.metrics is None:
                print(result.error)
                print()
                continue

            metrics = result.metrics
            print(metrics.filename)
            print(
                "  outer center=({:.2f}, {:.2f}) radius={:.2f}px circularity={:.4f}".format(
                    metrics.outer_center_x,
                    metrics.outer_center_y,
                    metrics.outer_radius_px,
                    metrics.outer_circularity,
                )
            )
            print(
                "  inner center=({:.2f}, {:.2f}) radius={:.2f}px circularity={:.4f}".format(
                    metrics.inner_center_x,
                    metrics.inner_center_y,
                    metrics.inner_radius_px,
                    metrics.inner_circularity,
                )
            )
            print(
                "  center offset={:.2f}px normalized_offset={:.4f} threshold_fraction={:.2f}".format(
                    metrics.center_offset_px,
                    metrics.normalized_offset,
                    metrics.threshold_fraction,
                )
            )
            print()
        return

    summaries = inspect_directory(
        image_dir=args.image_dir,
        preview_dir=args.preview_dir,
        write_previews=not args.skip_previews,
    )

    if args.json:
        print(json.dumps([asdict(summary) for summary in summaries], indent=2))
        return

    for summary in summaries:
        print(f"{summary.filename}")
        print(f"  shape={summary.shape} dtype={summary.dtype}")
        print(
            "  min={:.1f} max={:.1f} mean={:.2f} std={:.2f} p99.5={:.1f}".format(
                summary.min_value,
                summary.max_value,
                summary.mean_value,
                summary.std_value,
                summary.percentile_99_5,
            )
        )
        print(
            f"  peak=(x={summary.peak_x}, y={summary.peak_y}) peak_value={summary.peak_value:.1f}"
        )
        print(
            f"  exposure={summary.exposure_seconds} date_obs={summary.observation_time} instrument={summary.instrument}"
        )
        print()


def _normalize_for_preview(data: np.ndarray) -> np.ndarray:
    low = np.percentile(data, 1.0)
    high = np.percentile(data, 99.8)
    clipped = np.clip(data, low, high)
    scaled = (clipped - low) / max(high - low, 1e-6)
    return np.sqrt(scaled)


def _mask_circularity(mask: np.ndarray) -> float:
    area = float(np.count_nonzero(mask))
    perimeter = float(perimeter_crofton(mask, directions=4))
    if perimeter <= 0.0:
        return 0.0
    return float(4.0 * np.pi * area / (perimeter * perimeter))


def _find_best_ring_candidate(smooth: np.ndarray, source_name: str) -> tuple[float, RingMetrics] | None:
    background = float(np.median(smooth))
    peak = float(np.percentile(smooth, 99.999))
    best_candidate: RingMetrics | None = None
    best_score = -1.0

    for threshold_fraction in (0.08, 0.10, 0.12, 0.15, 0.18, 0.20, 0.25):
        threshold = background + threshold_fraction * (peak - background)
        binary = smooth > threshold
        binary = closing(binary, disk(3))
        binary = _remove_small_components(binary, minimum_size=200)

        labeled = label(binary)
        for region in sorted(regionprops(labeled), key=lambda item: item.area, reverse=True):
            if region.area < 2_000:
                continue

            ring_mask = labeled == region.label
            filled_mask = binary_fill_holes(ring_mask)
            hole_mask = filled_mask & ~ring_mask
            hole_labeled = label(hole_mask)
            holes = sorted(regionprops(hole_labeled), key=lambda item: item.area, reverse=True)
            if not holes:
                continue

            inner_region = holes[0]
            if inner_region.area < 500:
                continue

            inner_mask = hole_labeled == inner_region.label
            outer_circularity = _mask_circularity(ring_mask)
            inner_circularity = _mask_circularity(inner_mask)
            outer_radius = float(np.sqrt(region.area / np.pi))
            inner_radius = float(np.sqrt(inner_region.area / np.pi))
            hole_ratio = float(inner_region.area / max(region.area, 1.0))

            if outer_circularity < 0.2 or inner_circularity < 0.3 or hole_ratio < 0.01:
                continue

            center_offset = float(
                np.hypot(
                    region.centroid[1] - inner_region.centroid[1],
                    region.centroid[0] - inner_region.centroid[0],
                )
            )
            normalized_offset = center_offset / max(outer_radius, 1e-6)
            thickness_uniformity = _compute_thickness_uniformity(
                ring_mask,
                center_x=float(region.centroid[1]),
                center_y=float(region.centroid[0]),
            )
            brightness_balance, brightest_quadrant = _compute_brightness_balance(
                data=smooth,
                ring_mask=ring_mask,
                center_x=float(region.centroid[1]),
                center_y=float(region.centroid[0]),
            )
            outer_ellipse_ratio = _compute_ellipse_ratio(region.axis_major_length, region.axis_minor_length)
            inner_ellipse_ratio = _compute_ellipse_ratio(inner_region.axis_major_length, inner_region.axis_minor_length)

            score = (
                outer_circularity
                * inner_circularity
                * hole_ratio
                * np.sqrt(region.area)
            )
            if score <= best_score:
                continue

            best_score = score
            best_candidate = RingMetrics(
                filename=source_name,
                outer_center_x=float(region.centroid[1]),
                outer_center_y=float(region.centroid[0]),
                inner_center_x=float(inner_region.centroid[1]),
                inner_center_y=float(inner_region.centroid[0]),
                center_offset_px=center_offset,
                normalized_offset=float(normalized_offset),
                outer_radius_px=outer_radius,
                inner_radius_px=inner_radius,
                outer_circularity=float(outer_circularity),
                inner_circularity=float(inner_circularity),
                outer_area_px=float(region.area),
                inner_area_px=float(inner_region.area),
                threshold_fraction=float(threshold_fraction),
                thickness_uniformity=float(thickness_uniformity),
                brightness_balance=float(brightness_balance),
                brightest_quadrant=brightest_quadrant,
                outer_ellipse_ratio=float(outer_ellipse_ratio),
                inner_ellipse_ratio=float(inner_ellipse_ratio),
                detection_confidence=0.0,
                confidence_label="Unknown",
            )

    if best_candidate is None:
        return None

    return best_score, best_candidate


def _find_fast_candidate_via_ellipses(
    data: np.ndarray,
    smooth: np.ndarray,
    source_name: str,
) -> RingMetrics | None:
    image_data = np.nan_to_num(np.asarray(data, dtype=np.float32), copy=False)
    height, width = image_data.shape
    scale_factor = max(width, height) / 4096.0
    max_outer_radius = 300.0 * scale_factor
    max_inner_radius = 110.0 * scale_factor
    max_center_offset = 55.0 * scale_factor

    _, median, _ = sigma_clipped_stats(image_data, sigma=3.0, maxiters=5)
    vmin = float(median)
    vmax = float(np.percentile(image_data, 99.9))
    if not np.isfinite(vmax) or vmax <= vmin:
        return None

    image_data_clipped = np.clip(image_data, vmin, vmax)
    gray = ((image_data_clipped - vmin) / (vmax - vmin) * 255.0).astype(np.uint8)
    _, thresh = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    contours, _ = cv2.findContours(thresh, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return None

    min_contour_area = max(80.0, 400.0 * scale_factor * scale_factor)
    filtered_contours: list[np.ndarray] = []
    for contour in contours:
        if len(contour) < 5:
            continue
        area = cv2.contourArea(contour)
        if area < min_contour_area:
            continue
        filtered_contours.append(contour)

    if not filtered_contours:
        return None

    valid_ellipses: list[dict[str, float | tuple[tuple[float, float], tuple[float, float], float]]] = []
    for contour in sorted(filtered_contours, key=cv2.contourArea, reverse=True)[:80]:
        ellipse = cv2.fitEllipse(contour)
        (cx, cy), (d1, d2), angle = ellipse
        a = max(d1, d2) / 2.0
        b = min(d1, d2) / 2.0
        if a <= 0 or b <= 0:
            continue
        valid_ellipses.append(
            {
                "ellipse": ellipse,
                "cx": float(cx),
                "cy": float(cy),
                "a": float(a),
                "b": float(b),
                "angle": float(angle),
            }
        )

    best_pair: tuple[dict[str, float | tuple], dict[str, float | tuple], float] | None = None
    best_pair_score = -1.0
    for i in range(len(valid_ellipses)):
        for j in range(i + 1, len(valid_ellipses)):
            first = valid_ellipses[i]
            second = valid_ellipses[j]
            dist = float(np.hypot(first["cx"] - second["cx"], first["cy"] - second["cy"]))  # type: ignore[arg-type]
            if dist >= max_center_offset:
                continue

            outer, inner = (first, second) if first["a"] > second["a"] else (second, first)  # type: ignore[operator]
            if outer["a"] >= max_outer_radius or inner["a"] >= max_inner_radius:  # type: ignore[operator]
                continue
            if inner["a"] <= max(6.0, outer["a"] * 0.08):  # type: ignore[operator]
                continue

            outer_ellipse_ratio = _compute_ellipse_ratio(float(outer["a"]), float(outer["b"]))
            inner_ellipse_ratio = _compute_ellipse_ratio(float(inner["a"]), float(inner["b"]))
            pair_score = (
                float(outer["a"]) * 0.25 +
                float(inner["a"]) * 0.20 +
                (1.0 - min(dist / max_center_offset, 1.0)) * 120.0 +
                (1.0 - min(abs(outer_ellipse_ratio - 1.0), 1.0)) * 50.0 +
                (1.0 - min(abs(inner_ellipse_ratio - 1.0), 1.0)) * 40.0
            )
            if pair_score <= best_pair_score:
                continue

            best_pair_score = pair_score
            best_pair = (outer, inner, dist)

    if best_pair is None:
        return None

    outer, inner, dist = best_pair
    outer_mask = np.zeros((height, width), dtype=np.uint8)
    inner_mask = np.zeros((height, width), dtype=np.uint8)
    cv2.ellipse(outer_mask, outer["ellipse"], 1, thickness=-1)  # type: ignore[arg-type]
    cv2.ellipse(inner_mask, inner["ellipse"], 1, thickness=-1)  # type: ignore[arg-type]
    outer_mask_bool = outer_mask.astype(bool)
    inner_mask_bool = inner_mask.astype(bool)
    ring_mask = outer_mask_bool & ~inner_mask_bool
    if np.count_nonzero(ring_mask) < 100:
        return None

    thickness_uniformity = _compute_thickness_uniformity(
        ring_mask,
        center_x=float(outer["cx"]),
        center_y=float(outer["cy"]),
    )
    brightness_balance, brightest_quadrant = _compute_brightness_balance(
        data=smooth,
        ring_mask=ring_mask,
        center_x=float(outer["cx"]),
        center_y=float(outer["cy"]),
    )
    outer_area = float(np.count_nonzero(outer_mask_bool))
    inner_area = float(np.count_nonzero(inner_mask_bool))
    outer_circularity = _mask_circularity(outer_mask_bool)
    inner_circularity = _mask_circularity(inner_mask_bool)
    outer_radius = float(np.sqrt(outer_area / np.pi))
    inner_radius = float(np.sqrt(inner_area / np.pi))

    return RingMetrics(
        filename=source_name,
        outer_center_x=float(outer["cx"]),
        outer_center_y=float(outer["cy"]),
        inner_center_x=float(inner["cx"]),
        inner_center_y=float(inner["cy"]),
        center_offset_px=float(dist),
        normalized_offset=float(dist / max(outer_radius, 1e-6)),
        outer_radius_px=outer_radius,
        inner_radius_px=inner_radius,
        outer_circularity=float(outer_circularity),
        inner_circularity=float(inner_circularity),
        outer_area_px=outer_area,
        inner_area_px=inner_area,
        threshold_fraction=-1.0,
        thickness_uniformity=float(thickness_uniformity),
        brightness_balance=float(brightness_balance),
        brightest_quadrant=brightest_quadrant,
        outer_ellipse_ratio=_compute_ellipse_ratio(float(outer["a"]), float(outer["b"])),
        inner_ellipse_ratio=_compute_ellipse_ratio(float(inner["a"]), float(inner["b"])),
        detection_confidence=0.0,
        confidence_label="Unknown",
    )


def _preprocess_for_ring_detection(data: np.ndarray) -> np.ndarray:
    working = np.asarray(data, dtype=np.float32).copy()

    # Remove persistent vertical column offsets without disturbing the donut geometry.
    column_medians = np.median(working, axis=0)
    column_baseline = float(np.median(column_medians))
    column_offsets = column_medians - column_baseline
    column_sigma = float(np.std(column_offsets))
    if column_sigma > 1e-6:
        stripe_mask = np.abs(column_offsets) > (2.5 * column_sigma)
        working[:, stripe_mask] -= column_offsets[stripe_mask][None, :]

    # Flatten only very broad gradients so the donut hole is preserved.
    background = gaussian_filter_accelerated(working, sigma=96.0)
    background_offset = float(np.median(background))
    working = working - (background - background_offset) * 0.35

    # Softly compress the very brightest tail instead of hard clipping the ring structure.
    high_clip = float(np.percentile(working, 99.97))
    if np.isfinite(high_clip):
        over_mask = working > high_clip
        if np.any(over_mask):
            working[over_mask] = high_clip + np.sqrt(working[over_mask] - high_clip)

    return working


def _compute_ellipse_ratio(major_axis_length: float, minor_axis_length: float) -> float:
    if minor_axis_length <= 1e-6:
        return 0.0
    return float(major_axis_length / minor_axis_length)


def _compute_thickness_uniformity(ring_mask: np.ndarray, center_x: float, center_y: float) -> float:
    y_indices, x_indices = np.nonzero(ring_mask)
    radii = np.hypot(x_indices - center_x, y_indices - center_y)
    angles = (np.degrees(np.arctan2(y_indices - center_y, x_indices - center_x)) + 360.0) % 360.0

    thicknesses: list[float] = []
    for angle_start in np.arange(0.0, 360.0, 10.0):
        in_bin = (angles >= angle_start) & (angles < angle_start + 10.0)
        if np.count_nonzero(in_bin) < 10:
            continue
        bin_radii = radii[in_bin]
        thicknesses.append(float(np.percentile(bin_radii, 95) - np.percentile(bin_radii, 5)))

    if not thicknesses:
        return 0.0

    thickness_array = np.asarray(thicknesses, dtype=np.float64)
    mean_thickness = float(np.mean(thickness_array))
    if mean_thickness <= 1e-6:
        return 0.0

    coefficient_of_variation = float(np.std(thickness_array) / mean_thickness)
    return float(np.clip(1.0 - coefficient_of_variation, 0.0, 1.0))


def _compute_brightness_balance(
    data: np.ndarray,
    ring_mask: np.ndarray,
    center_x: float,
    center_y: float,
) -> tuple[float, str]:
    y_indices, x_indices = np.nonzero(ring_mask)
    if x_indices.size == 0:
        return 0.0, "Unknown"

    quadrant_samples: dict[str, list[float]] = {
        "NE": [],
        "NW": [],
        "SE": [],
        "SW": [],
    }

    for y, x in zip(y_indices, x_indices):
        if x >= center_x and y < center_y:
            quadrant_samples["NE"].append(float(data[y, x]))
        elif x < center_x and y < center_y:
            quadrant_samples["NW"].append(float(data[y, x]))
        elif x >= center_x and y >= center_y:
            quadrant_samples["SE"].append(float(data[y, x]))
        else:
            quadrant_samples["SW"].append(float(data[y, x]))

    quadrant_means = {
        quadrant: (float(np.mean(samples)) if samples else 0.0)
        for quadrant, samples in quadrant_samples.items()
    }

    brightest_quadrant = max(quadrant_means, key=quadrant_means.get)
    min_mean = min(quadrant_means.values())
    max_mean = max(quadrant_means.values())
    if max_mean <= 1e-6:
        return 0.0, brightest_quadrant

    balance = min_mean / max_mean
    return float(np.clip(balance, 0.0, 1.0)), brightest_quadrant


def _refine_circle_from_fixed_center(
    data: np.ndarray,
    initial_center_x: float,
    initial_center_y: float,
    expected_radius: float,
    search_inner_radius: float,
    search_outer_radius: float,
    edge_polarity: str,
) -> tuple[float, float, float, float] | None:
    candidate_points: list[tuple[float, float]] = []
    candidate_radii: list[float] = []
    candidate_strengths: list[float] = []

    for angle_degrees in np.arange(0.0, 360.0, 2.0):
        theta = np.deg2rad(angle_degrees)
        radii = np.linspace(search_inner_radius, search_outer_radius, 260)
        x_positions = initial_center_x + (radii * np.cos(theta))
        y_positions = initial_center_y + (radii * np.sin(theta))

        x_indices = np.clip(np.round(x_positions).astype(int), 0, data.shape[1] - 1)
        y_indices = np.clip(np.round(y_positions).astype(int), 0, data.shape[0] - 1)
        profile = data[y_indices, x_indices]
        gradient = np.gradient(profile)

        if edge_polarity == "rising":
            best_index = int(np.argmax(gradient))
            strength = float(gradient[best_index])
            if strength <= np.percentile(gradient, 65):
                continue
        else:
            best_index = int(np.argmin(gradient))
            strength = float(-gradient[best_index])
            if -gradient[best_index] <= np.percentile(-gradient, 65):
                continue

        candidate_points.append((float(x_positions[best_index]), float(y_positions[best_index])))
        candidate_radii.append(float(radii[best_index]))
        candidate_strengths.append(strength)

    if len(candidate_points) < 24:
        return None

    radii_array = np.asarray(candidate_radii, dtype=np.float64)
    strengths_array = np.asarray(candidate_strengths, dtype=np.float64)
    median_radius = float(np.median(radii_array))
    mad_radius = float(np.median(np.abs(radii_array - median_radius)))
    if mad_radius <= 1e-6:
        inlier_indices = np.arange(radii_array.size)
    else:
        inlier_indices = np.where(np.abs(radii_array - median_radius) <= max(2.5 * mad_radius, expected_radius * 0.18))[0]

    if inlier_indices.size < 18:
        return None

    inlier_points = [candidate_points[index] for index in inlier_indices]
    fitted_center_x, fitted_center_y, fitted_radius = _fit_circle_least_squares(inlier_points)
    if not np.isfinite(fitted_center_x) or not np.isfinite(fitted_center_y) or not np.isfinite(fitted_radius):
        return None

    point_array = np.asarray(inlier_points, dtype=np.float64)
    point_radii = np.hypot(point_array[:, 0] - fitted_center_x, point_array[:, 1] - fitted_center_y)
    residual_std = float(np.std(point_radii))
    radius_consistency = float(np.clip(1.0 - (residual_std / max(expected_radius * 0.20, 1e-6)), 0.0, 1.0))
    inlier_ratio = float(inlier_indices.size / max(len(candidate_points), 1))
    strength_score = float(
        np.clip(
            np.median(strengths_array[inlier_indices]) /
            max(np.percentile(strengths_array, 90), 1e-6),
            0.0,
            1.0,
        )
    )
    confidence = float(np.clip((inlier_ratio * 0.42) + (radius_consistency * 0.38) + (strength_score * 0.20), 0.0, 1.0))
    return float(fitted_center_x), float(fitted_center_y), float(fitted_radius), confidence


def _search_inner_circle_from_radial_edges(
    data: np.ndarray,
    initial_center_x: float,
    initial_center_y: float,
    expected_radius: float,
    search_inner_radius: float,
    search_outer_radius: float,
    edge_polarity: str,
) -> tuple[float, float, float, float] | None:
    search_extent = max(expected_radius * 0.16, 6.0)
    coarse_step = max(expected_radius / 8.0, 3.0)
    coarse_result = _best_inner_center_candidate(
        data=data,
        initial_center_x=initial_center_x,
        initial_center_y=initial_center_y,
        expected_radius=expected_radius,
        search_inner_radius=search_inner_radius,
        search_outer_radius=search_outer_radius,
        edge_polarity=edge_polarity,
        search_extent=search_extent,
        step=coarse_step,
    )
    if coarse_result is None:
        return None

    coarse_center_x, coarse_center_y, _, _ = coarse_result
    fine_result = _best_inner_center_candidate(
        data=data,
        initial_center_x=coarse_center_x,
        initial_center_y=coarse_center_y,
        expected_radius=expected_radius,
        search_inner_radius=search_inner_radius,
        search_outer_radius=search_outer_radius,
        edge_polarity=edge_polarity,
        search_extent=max(coarse_step * 1.5, 3.0),
        step=max(coarse_step / 2.0, 1.5),
    )
    return fine_result or coarse_result


def _best_inner_center_candidate(
    data: np.ndarray,
    initial_center_x: float,
    initial_center_y: float,
    expected_radius: float,
    search_inner_radius: float,
    search_outer_radius: float,
    edge_polarity: str,
    search_extent: float,
    step: float,
) -> tuple[float, float, float, float] | None:
    best_result: tuple[float, float, float, float] | None = None
    best_score = -np.inf

    x_offsets = np.arange(-search_extent, search_extent + (step * 0.5), step)
    y_offsets = np.arange(-search_extent, search_extent + (step * 0.5), step)

    for x_offset in x_offsets:
        for y_offset in y_offsets:
            candidate = _evaluate_inner_center_candidate(
                data=data,
                center_x=initial_center_x + float(x_offset),
                center_y=initial_center_y + float(y_offset),
                expected_radius=expected_radius,
                search_inner_radius=search_inner_radius,
                search_outer_radius=search_outer_radius,
                edge_polarity=edge_polarity,
                angle_step=6.0,
            )
            if candidate is None:
                continue

            center_x, center_y, radius, candidate_score, confidence = candidate
            center_shift = np.hypot(center_x - initial_center_x, center_y - initial_center_y)
            score = candidate_score - (center_shift / max(expected_radius, 1e-6)) * 0.22
            if score <= best_score:
                continue

            best_score = score
            best_result = (center_x, center_y, radius, confidence)

    return best_result


def _evaluate_inner_center_candidate(
    data: np.ndarray,
    center_x: float,
    center_y: float,
    expected_radius: float,
    search_inner_radius: float,
    search_outer_radius: float,
    edge_polarity: str,
    angle_step: float,
) -> tuple[float, float, float, float, float] | None:
    candidate_points: list[tuple[float, float]] = []
    candidate_radii: list[float] = []
    candidate_strengths: list[float] = []

    for angle_degrees in np.arange(0.0, 360.0, angle_step):
        theta = np.deg2rad(angle_degrees)
        radii = np.linspace(search_inner_radius, search_outer_radius, 260)
        x_positions = center_x + (radii * np.cos(theta))
        y_positions = center_y + (radii * np.sin(theta))

        x_indices = np.clip(np.round(x_positions).astype(int), 0, data.shape[1] - 1)
        y_indices = np.clip(np.round(y_positions).astype(int), 0, data.shape[0] - 1)
        profile = data[y_indices, x_indices]
        gradient = np.gradient(profile)

        if edge_polarity == "rising":
            best_index = int(np.argmax(gradient))
            strength = float(gradient[best_index])
            if strength <= np.percentile(gradient, 60):
                continue
        else:
            best_index = int(np.argmin(gradient))
            strength = float(-gradient[best_index])
            if -gradient[best_index] <= np.percentile(-gradient, 60):
                continue

        candidate_points.append((float(x_positions[best_index]), float(y_positions[best_index])))
        candidate_radii.append(float(radii[best_index]))
        candidate_strengths.append(strength)

    if len(candidate_points) < 22:
        return None

    radii_array = np.asarray(candidate_radii, dtype=np.float64)
    strengths_array = np.asarray(candidate_strengths, dtype=np.float64)
    median_radius = float(np.median(radii_array))
    mad_radius = float(np.median(np.abs(radii_array - median_radius)))
    if mad_radius <= 1e-6:
        inlier_indices = np.arange(radii_array.size)
    else:
        inlier_indices = np.where(np.abs(radii_array - median_radius) <= max(2.5 * mad_radius, expected_radius * 0.16))[0]

    if inlier_indices.size < 18:
        return None

    inlier_points = [candidate_points[index] for index in inlier_indices]
    fitted_center_x, fitted_center_y, fitted_radius = _fit_circle_least_squares(inlier_points)

    if not np.isfinite(fitted_center_x) or not np.isfinite(fitted_center_y) or not np.isfinite(fitted_radius):
        return None

    radius_drift = float(abs(fitted_radius - expected_radius) / max(expected_radius, 1e-6))
    if radius_drift > 0.38:
        return None

    point_array = np.asarray(inlier_points, dtype=np.float64)
    point_radii = np.hypot(point_array[:, 0] - fitted_center_x, point_array[:, 1] - fitted_center_y)
    residual_std = float(np.std(point_radii))
    radius_consistency = float(np.clip(1.0 - (residual_std / max(expected_radius * 0.18, 1e-6)), 0.0, 1.0))
    inlier_ratio = float(inlier_indices.size / max(len(candidate_points), 1))
    radius_match_score = float(np.clip(1.0 - (radius_drift / 0.38), 0.0, 1.0))
    strength_score = float(
        np.clip(
            np.median(strengths_array[inlier_indices]) /
            max(np.percentile(strengths_array, 90), 1e-6),
            0.0,
            1.0,
        )
    )
    confidence = float(
        np.clip(
            (inlier_ratio * 0.34) +
            (radius_consistency * 0.28) +
            (radius_match_score * 0.26) +
            (strength_score * 0.12),
            0.0,
            1.0,
        )
    )
    candidate_score = float(
        (radius_consistency * 1.0) +
        (inlier_ratio * 0.9) +
        (radius_match_score * 0.9) +
        (strength_score * 0.35)
    )
    return float(fitted_center_x), float(fitted_center_y), float(fitted_radius), candidate_score, confidence


def _choose_best_inner_refinement(
    fixed_center_refined: tuple[float, float, float, float, float] | None,
    searched_center_refined: tuple[float, float, float, float] | None,
    expected_radius: float,
) -> tuple[float, float, float, float] | None:
    if fixed_center_refined is None:
        return searched_center_refined
    if searched_center_refined is None:
        return fixed_center_refined[:4]

    fixed_radius_drift = abs(fixed_center_refined[2] - expected_radius) / max(expected_radius, 1e-6)
    search_radius_drift = abs(searched_center_refined[2] - expected_radius) / max(expected_radius, 1e-6)
    fixed_confidence = fixed_center_refined[3]
    searched_confidence = searched_center_refined[3]

    if (
        searched_confidence > fixed_confidence + 0.08 and
        search_radius_drift <= fixed_radius_drift + 0.10
    ):
        return searched_center_refined

    return fixed_center_refined[:4]


def _compute_detection_confidence(
    normalized_offset: float,
    outer_circularity: float,
    inner_circularity: float,
    thickness_uniformity: float,
    brightness_balance: float,
    outer_ellipse_ratio: float,
    inner_ellipse_ratio: float,
    refinement_confidence: float,
) -> float:
    outer_shape_score = np.clip(outer_circularity / 0.75, 0.0, 1.0)
    inner_shape_score = np.clip(inner_circularity / 0.95, 0.0, 1.0)
    outer_ellipse_score = np.clip(1.0 - ((outer_ellipse_ratio - 1.0) / 0.18), 0.0, 1.0)
    inner_ellipse_score = np.clip(1.0 - ((inner_ellipse_ratio - 1.0) / 0.18), 0.0, 1.0)
    asymmetry_penalty = np.clip(normalized_offset / 0.22, 0.0, 1.0)

    confidence = (
        (outer_shape_score * 0.18) +
        (inner_shape_score * 0.14) +
        (np.clip(thickness_uniformity, 0.0, 1.0) * 0.18) +
        (np.clip(brightness_balance, 0.0, 1.0) * 0.12) +
        (outer_ellipse_score * 0.10) +
        (inner_ellipse_score * 0.08) +
        (np.clip(refinement_confidence, 0.0, 1.0) * 0.20)
    )
    confidence *= 1.0 - (asymmetry_penalty * 0.15)
    return float(np.clip(confidence, 0.0, 1.0))


def _confidence_label(confidence: float) -> str:
    if confidence >= 0.85:
        return "High"
    if confidence >= 0.65:
        return "Medium"
    return "Low"


def _fit_circle_least_squares(points: list[tuple[float, float]]) -> tuple[float, float, float]:
    point_array = np.asarray(points, dtype=np.float64)
    x = point_array[:, 0]
    y = point_array[:, 1]
    system_matrix = np.column_stack((2.0 * x, 2.0 * y, np.ones_like(x)))
    right_hand_side = (x * x) + (y * y)
    solution, *_ = np.linalg.lstsq(system_matrix, right_hand_side, rcond=None)
    center_x, center_y, offset = solution
    radius = np.sqrt(max(offset + (center_x * center_x) + (center_y * center_y), 0.0))
    return float(center_x), float(center_y), float(radius)


def _remove_small_components(mask: np.ndarray, minimum_size: int) -> np.ndarray:
    labeled = label(mask)
    filtered = np.zeros_like(mask, dtype=bool)
    for region in regionprops(labeled):
        if region.area >= minimum_size:
            filtered[labeled == region.label] = True
    return filtered


def _as_float(value: Any) -> float | None:
    if value is None:
        return None
    return float(value)


def _as_str(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


if __name__ == "__main__":
    main()
