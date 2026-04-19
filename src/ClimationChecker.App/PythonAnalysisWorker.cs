using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClimationChecker.App;

internal sealed class PythonAnalysisWorker : IDisposable
{
    private readonly string _repositoryRoot;
    private readonly string _pythonModulePath;
    private readonly Action<double, string, string> _progressCallback;
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private Task? _stderrPumpTask;
    private TaskCompletionSource<bool>? _readySignal;
    private StringBuilder _errorBuffer = new();

    public PythonAnalysisWorker(string repositoryRoot, string pythonModulePath, Action<double, string, string> progressCallback)
    {
        _repositoryRoot = repositoryRoot;
        _pythonModulePath = pythonModulePath;
        _progressCallback = progressCallback;
    }

    public async Task<ViewerAnalysisResult> AnalyzeAsync(FrameSource frameSource, string outputDirectory)
    {
        await _requestLock.WaitAsync();
        try
        {
            EnsureWorkerStarted();
            ResetErrorBuffer();

            var request = new WorkerAnalyzeRequest
            {
                Command = "analyze",
                SourcePath = frameSource.FramePath,
                RawMetadataPath = frameSource.Kind == FrameSourceKind.Raw ? frameSource.MetadataPath : null,
                OutputDir = outputDirectory,
            };

            var requestJson = JsonSerializer.Serialize(request);
            await _stdin!.WriteLineAsync(requestJson);
            await _stdin.FlushAsync();

            var responseLine = await _stdout!.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new InvalidOperationException(BuildWorkerError("Python worker did not return a response."));
            }

            var result = JsonSerializer.Deserialize<ViewerAnalysisResult>(responseLine, MainWindow.JsonOptions);
            if (result is null)
            {
                throw new InvalidOperationException(BuildWorkerError("Python worker returned invalid JSON."));
            }

            if (!string.IsNullOrWhiteSpace(result.Error) && string.IsNullOrWhiteSpace(result.CropDataFile))
            {
                throw new InvalidOperationException(BuildWorkerError(result.Error));
            }

            return result;
        }
        catch
        {
            RestartWorker();
            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        _requestLock.Wait();
        try
        {
            ShutdownWorker();
        }
        finally
        {
            _requestLock.Release();
            _requestLock.Dispose();
        }
    }

    private void EnsureWorkerStarted()
    {
        if (_process is { HasExited: false } && _stdin is not null && _stdout is not null)
        {
            return;
        }

        ShutdownWorker();

        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = _repositoryRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.Environment["PYTHONPATH"] = _pythonModulePath;
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("climation_checker.analysis_worker");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Python analysis worker.");
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stderrPumpTask = Task.Run(() => PumpWorkerErrorStream(_process.StandardError, _readySignal));

        if (!_readySignal.Task.Wait(TimeSpan.FromSeconds(10)) || !_readySignal.Task.Result)
        {
            throw new InvalidOperationException(BuildWorkerError("Python worker did not become ready."));
        }
    }

    private void PumpWorkerErrorStream(StreamReader errorReader, TaskCompletionSource<bool> readySignal)
    {
        while (true)
        {
            var line = errorReader.ReadLine();
            if (line is null)
            {
                readySignal.TrySetResult(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line == "READY")
            {
                readySignal.TrySetResult(true);
                continue;
            }

            if (TryParsePythonProgress(line, out var percent, out var headline, out var detail))
            {
                _progressCallback(percent, headline, detail);
                continue;
            }

            lock (_errorBuffer)
            {
                _errorBuffer.AppendLine(line);
            }
        }
    }

    private void RestartWorker()
    {
        ShutdownWorker();
    }

    private void ShutdownWorker()
    {
        try
        {
            if (_process is { HasExited: false } && _stdin is not null)
            {
                var shutdown = JsonSerializer.Serialize(new { command = "shutdown" });
                _stdin.WriteLine(shutdown);
                _stdin.Flush();
            }
        }
        catch
        {
        }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1000);
            }
        }
        catch
        {
        }

        _stdin?.Dispose();
        _stdout?.Dispose();
        _stderrPumpTask = null;
        _readySignal = null;
        _process?.Dispose();
        _stdin = null;
        _stdout = null;
        _process = null;
    }

    private void ResetErrorBuffer()
    {
        lock (_errorBuffer)
        {
            _errorBuffer.Clear();
        }
    }

    private string BuildWorkerError(string fallback)
    {
        lock (_errorBuffer)
        {
            var errorText = _errorBuffer.ToString().Trim();
            return string.IsNullOrWhiteSpace(errorText) ? fallback : errorText;
        }
    }

    private static bool TryParsePythonProgress(string line, out double percent, out string headline, out string detail)
    {
        percent = 0;
        headline = string.Empty;
        detail = string.Empty;

        if (!line.StartsWith("PROGRESS|", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = line.Split('|', 4, StringSplitOptions.None);
        if (parts.Length != 4 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
        {
            return false;
        }

        headline = parts[2];
        detail = parts[3];
        return true;
    }
}

internal sealed class WorkerAnalyzeRequest
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("raw_metadata_path")]
    public string? RawMetadataPath { get; set; }

    [JsonPropertyName("output_dir")]
    public string OutputDir { get; set; } = string.Empty;
}
