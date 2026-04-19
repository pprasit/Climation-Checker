# Climation Checker

Small Python utilities for reading and inspecting FITS images that will later feed a collimation-checking workflow.

The repository now also includes a standalone `FLI` camera control layer in C# under [src/ClimationChecker.Fli](C:/Users/pakaw/OneDrive/Documents/GitHub/Climation-Checker/src/ClimationChecker.Fli/ClimationChecker.Fli.csproj:1) and a small CLI in [src/ClimationChecker.Fli.Cli](C:/Users/pakaw/OneDrive/Documents/GitHub/Climation-Checker/src/ClimationChecker.Fli.Cli/ClimationChecker.Fli.Cli.csproj:1). This was derived from the connection flow in `CCD_FLI`, but trimmed down so the new project talks to `libfli64.dll` directly without the old daemon and networking dependencies.

## Quick start

```powershell
python -m pip install -r requirements.txt
python src/climation_checker/fits_inspector.py
```

The script scans the `Image/` folder, prints a summary for each `.fit` file, and writes stretched preview PNG files to `output/previews/`.

To estimate donut-ring geometry for defocused stars, run:

```powershell
python src/climation_checker/fits_inspector.py --analyze-rings
```

That command computes outer and inner ring centers, center offset, normalized concentricity, and circularity, then writes overlay images to `output/rings/`.

## FLI camera control

Build the C# projects:

```powershell
dotnet build ClimationChecker.sln -c Release
```

List connected Finger Lakes CCD cameras:

```powershell
dotnet run --project src/ClimationChecker.Fli.Cli -- list
```

Read a camera status snapshot:

```powershell
dotnet run --project src/ClimationChecker.Fli.Cli -- status --serial <serial-number>
```

Capture a `RAW 16-bit` frame and metadata JSON for Python processing:

```powershell
dotnet run --project src/ClimationChecker.Fli.Cli -- capture --serial <serial-number> --output output/captures/procyon --exposure-ms 1000 --hbin 1 --vbin 1 --frame-type Normal
```

The capture command writes:

- `output/captures/procyon.raw` as little-endian `uint16` grayscale pixels
- `output/captures/procyon.json` with width, height, exposure, binning, serial number, and capture timestamp

That `RAW + JSON` pair is the intended handoff into the Python image-processing pipeline.

## Simulation UI

The repository also includes a WPF simulation viewer in [src/ClimationChecker.App](C:/Users/pakaw/OneDrive/Documents/GitHub/Climation-Checker/src/ClimationChecker.App/ClimationChecker.App.csproj:1). It loads random FITS files from `Image/`, asks the Python analyzer to generate a stretched preview, and overlays the detected inner and outer rings.

Run it with:

```powershell
dotnet run --project src/ClimationChecker.App -c Release
```

The UI supports:

- `Expose` as the primary action
- automatic fallback to simulation mode when no FLI camera is connected
- random simulation frames from the `Image` folder
- live `RAW 16-bit + JSON` capture handoff when an FLI camera is available
- MaxIm DL-style stretch profiles: `Low`, `Medium`, `High`, `Moon`, `Planet`, `Max Val`, `Range`, `Floating`, `Manual`
- auto-refresh while moving stretch sliders
- preview rendering through Python
- analysis readout for offset, circularity, and radii
- cyan and magenta overlay circles for the detected outer and inner rings
