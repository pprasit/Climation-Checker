from __future__ import annotations

import os
import warnings
from functools import lru_cache

import numpy as np
from scipy.ndimage import gaussian_filter as scipy_gaussian_filter


@lru_cache(maxsize=1)
def _gpu_runtime() -> tuple[bool, str]:
    use_gpu = os.environ.get("CLIMATION_USE_GPU", "1").strip().lower()
    if use_gpu in {"0", "false", "no", "off"}:
        return False, "CPU"

    try:
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore", message="CUDA path could not be detected.*")
            import cupy  # type: ignore
            import cupyx.scipy.ndimage as cupy_ndimage  # type: ignore

        _ = cupy.cuda.runtime.getDeviceCount()
        return True, f"GPU ({cupy.__name__})"
    except Exception:
        return False, "CPU"


def backend_name() -> str:
    return _gpu_runtime()[1]


def gaussian_filter_accelerated(data: np.ndarray, sigma: float) -> np.ndarray:
    gpu_available, _ = _gpu_runtime()
    if not gpu_available:
        return scipy_gaussian_filter(data, sigma=sigma)

    try:
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore", message="CUDA path could not be detected.*")
            import cupy  # type: ignore
            import cupyx.scipy.ndimage as cupy_ndimage  # type: ignore

        gpu_array = cupy.asarray(np.asarray(data, dtype=np.float32))
        filtered = cupy_ndimage.gaussian_filter(gpu_array, sigma=sigma)
        return cupy.asnumpy(filtered).astype(np.float32, copy=False)
    except Exception:
        return scipy_gaussian_filter(data, sigma=sigma)


def argmax_position(data: np.ndarray, sigma: float | None = None) -> tuple[int, int]:
    array = np.asarray(data, dtype=np.float32)
    if sigma is not None and sigma > 0:
        array = gaussian_filter_accelerated(array, sigma=sigma)

    peak_y, peak_x = np.unravel_index(np.argmax(array), array.shape)
    return int(peak_x), int(peak_y)
