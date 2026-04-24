from __future__ import annotations

import argparse
from pathlib import Path

import matplotlib

matplotlib.use("Agg")

import matplotlib.pyplot as plt
import numpy as np
from mpl_toolkits.mplot3d import Axes3D  # noqa: F401


MAX_SURFACE_DIMENSION = 160


def load_crop_data(path: Path, width: int, height: int) -> np.ndarray:
    data = np.fromfile(path, dtype="<f4")
    expected = width * height
    if data.size != expected:
        raise ValueError(f"Expected {expected} float32 values, found {data.size}.")
    return data.reshape((height, width))


def downsample_for_surface(data: np.ndarray) -> np.ndarray:
    stride = max(1, int(np.ceil(max(data.shape) / MAX_SURFACE_DIMENSION)))
    return np.asarray(data[::stride, ::stride], dtype=np.float32)


def render_surface_plot(data: np.ndarray, output_path: Path) -> None:
    reduced = downsample_for_surface(data)
    height, width = reduced.shape
    y_coords, x_coords = np.mgrid[0:height, 0:width]

    figure = plt.figure(figsize=(10, 7), dpi=160)
    axis = figure.add_subplot(111, projection="3d")
    surface = axis.plot_surface(
        x_coords,
        y_coords,
        reduced,
        cmap="inferno",
        linewidth=0,
        antialiased=True,
        rcount=height,
        ccount=width,
    )

    axis.set_xlabel("Pixel X", labelpad=12)
    axis.set_ylabel("Pixel Y", labelpad=12)
    axis.set_zlabel("Intensity", labelpad=12)
    axis.set_title("Donut Intensity Surface", pad=18)
    axis.view_init(elev=38, azim=-135)
    axis.grid(False)
    figure.colorbar(surface, shrink=0.72, pad=0.08, label="Intensity")
    figure.tight_layout()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    figure.savefig(output_path, bbox_inches="tight", facecolor="#0E141A")
    plt.close(figure)


def main() -> None:
    parser = argparse.ArgumentParser(description="Render a 3D donut intensity surface plot.")
    parser.add_argument("--input", type=Path, required=True, help="Path to the float32 crop data file.")
    parser.add_argument("--width", type=int, required=True, help="Crop width in pixels.")
    parser.add_argument("--height", type=int, required=True, help="Crop height in pixels.")
    parser.add_argument("--output", type=Path, required=True, help="Path to the output PNG file.")
    args = parser.parse_args()

    crop = load_crop_data(args.input, args.width, args.height)
    render_surface_plot(crop, args.output)


if __name__ == "__main__":
    main()
