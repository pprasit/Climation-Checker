using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ClimationChecker.Fli;

internal sealed class FliCameraSdk : IDisposable
{
    private const string LibraryName = "libfli64.dll";
    private const int MaxStringLength = 256;
    private const int InvalidDevice = -1;

    private IntPtr _deviceHandle = (IntPtr)InvalidDevice;
    private bool _disposed;

    public FliCameraSdk(string fileName, FliDomain domain)
    {
        var status = FLIOpen(out _deviceHandle, fileName, domain);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public static IReadOnlyList<FliCameraDescriptor> ListUsbCameras()
    {
        var status = FLIList(FliDomain.Camera | FliDomain.Usb, out var namesHandle);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }

        try
        {
            var cameras = new List<FliCameraDescriptor>();
            var cursor = namesHandle;
            while (cursor != IntPtr.Zero)
            {
                var wrapper = Marshal.PtrToStructure<StringWrapper>(cursor);
                if (wrapper?.Value is null)
                {
                    break;
                }

                var value = wrapper.Value;
                var delimiter = value.IndexOf(';');
                var fileName = delimiter < 0 ? value : value[..delimiter];
                var modelName = delimiter < 0 ? null : value[(delimiter + 1)..];

                using var camera = new FliCameraSdk(fileName, FliDomain.Camera | FliDomain.Usb);
                cameras.Add(new FliCameraDescriptor(fileName, modelName, camera.GetSerialString()));
                cursor += IntPtr.Size;
            }

            return cameras;
        }
        finally
        {
            FLIFreeList(namesHandle);
        }
    }

    public string? GetModel()
    {
        var builder = new StringBuilder(MaxStringLength);
        var status = FLIGetModel(_deviceHandle, builder, builder.Capacity);
        return status == 0 ? builder.ToString() : null;
    }

    public string GetSerialString()
    {
        var builder = new StringBuilder(MaxStringLength);
        var status = FLIGetSerialString(_deviceHandle, builder, builder.Capacity);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }

        return builder.ToString();
    }

    public void GetVisibleArea(out int upperLeftX, out int upperLeftY, out int lowerRightX, out int lowerRightY)
    {
        var status = FLIGetVisibleArea(_deviceHandle, out upperLeftX, out upperLeftY, out lowerRightX, out lowerRightY);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void SetImageArea(FliReadoutArea area)
    {
        var status = FLISetImageArea(_deviceHandle, area.UpperLeftX, area.UpperLeftY, area.LowerRightX, area.LowerRightY);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void SetHorizontalBin(int horizontalBin)
    {
        var status = FLISetHBin(_deviceHandle, horizontalBin);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void SetVerticalBin(int verticalBin)
    {
        var status = FLISetVBin(_deviceHandle, verticalBin);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void SetFrameType(FliFrameType frameType)
    {
        var status = FLISetFrameType(_deviceHandle, frameType);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void SetExposureTime(int exposureMilliseconds)
    {
        var status = FLISetExposureTime(_deviceHandle, exposureMilliseconds);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void SetTdi(int tdiRate)
    {
        var status = FLISetTDI(_deviceHandle, tdiRate, 0);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void SetFlushCount(int flushCount)
    {
        var status = FLISetNFlushes(_deviceHandle, flushCount);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void ExposeFrame()
    {
        var status = FLIExposeFrame(_deviceHandle);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public int GetExposureStatus()
    {
        var status = FLIGetExposureStatus(_deviceHandle, out var timeLeft);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }

        return timeLeft;
    }

    public FliDeviceStatus GetDeviceStatus()
    {
        var status = FLIGetDeviceStatus(_deviceHandle, out var deviceStatus);
        if (status != 0)
        {
            return FliDeviceStatus.CameraStatusUnknown;
        }

        return (FliDeviceStatus)deviceStatus;
    }

    public bool IsDownloadReady(out int remainingExposureMilliseconds)
    {
        var status = GetDeviceStatus();
        remainingExposureMilliseconds = GetExposureStatus();

        return ((status == FliDeviceStatus.CameraStatusUnknown) && (remainingExposureMilliseconds == 0)) ||
               ((status != FliDeviceStatus.CameraStatusUnknown) &&
                (status & FliDeviceStatus.CameraDataReady) != 0);
    }

    public void GrabRow(ushort[] rowBuffer)
    {
        var handle = GCHandle.Alloc(rowBuffer, GCHandleType.Pinned);
        try
        {
            var status = FLIGrabRow(_deviceHandle, handle.AddrOfPinnedObject(), rowBuffer.Length);
            if (status != 0)
            {
                throw new Win32Exception(-status);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    public FliReadoutDimensions GetReadoutDimensions()
    {
        var status = FLIGetReadoutDimensions(
            _deviceHandle,
            out var width,
            out var horizontalOffset,
            out var horizontalBin,
            out var height,
            out var verticalOffset,
            out var verticalBin);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }

        return new FliReadoutDimensions(width, horizontalOffset, horizontalBin, height, verticalOffset, verticalBin);
    }

    public FliReadoutDimensions? TryGetReadoutDimensions()
    {
        try
        {
            return GetReadoutDimensions();
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public void CancelExposure()
    {
        var status = FLICancelExposure(_deviceHandle);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public double GetTemperature()
    {
        var status = FLIGetTemperature(_deviceHandle, out var temperature);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }

        return temperature;
    }

    public void SetTemperature(double temperature)
    {
        var status = FLISetTemperature(_deviceHandle, temperature);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public double GetCoolerPower()
    {
        var status = FLIGetCoolerPower(_deviceHandle, out var power);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }

        return power;
    }

    public void SetBackgroundFlush(FliBackgroundFlush mode)
    {
        var status = FLIControlBackgroundFlush(_deviceHandle, mode);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public int GetCameraMode()
    {
        var status = FLIGetCameraMode(_deviceHandle, out var modeIndex);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }

        return modeIndex;
    }

    public string? GetCameraModeString(int modeIndex)
    {
        var builder = new StringBuilder(MaxStringLength);
        var status = FLIGetCameraModeString(_deviceHandle, modeIndex, builder, builder.Capacity);
        return status == 0 ? builder.ToString() : null;
    }

    public void SetCameraMode(int modeIndex)
    {
        var status = FLISetCameraMode(_deviceHandle, modeIndex);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void LockDevice()
    {
        var status = FLILockDevice(_deviceHandle);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void UnlockDevice()
    {
        var status = FLIUnlockDevice(_deviceHandle);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }
    }

    public void Close()
    {
        if (_deviceHandle == (IntPtr)InvalidDevice)
        {
            return;
        }

        var status = FLIClose(_deviceHandle);
        if (status != 0)
        {
            throw new Win32Exception(-status);
        }

        _deviceHandle = (IntPtr)InvalidDevice;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~FliCameraSdk()
    {
        if (!_disposed)
        {
            try
            {
                Close();
            }
            catch
            {
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class StringWrapper
    {
        public string? Value;
    }

    [DllImport(LibraryName)]
    private static extern int FLIOpen(out IntPtr dev, string name, FliDomain domain);

    [DllImport(LibraryName)]
    private static extern int FLIClose(IntPtr dev);

    [DllImport(LibraryName)]
    private static extern int FLIList(FliDomain domain, out IntPtr names);

    [DllImport(LibraryName)]
    private static extern int FLIFreeList(IntPtr names);

    [DllImport(LibraryName)]
    private static extern int FLIGetModel(IntPtr dev, StringBuilder model, int len);

    [DllImport(LibraryName)]
    private static extern int FLIGetSerialString(IntPtr dev, StringBuilder serial, int len);

    [DllImport(LibraryName)]
    private static extern int FLIGetVisibleArea(IntPtr dev, out int ulX, out int ulY, out int lrX, out int lrY);

    [DllImport(LibraryName)]
    private static extern int FLISetImageArea(IntPtr dev, int ulX, int ulY, int lrX, int lrY);

    [DllImport(LibraryName)]
    private static extern int FLISetHBin(IntPtr dev, int hbin);

    [DllImport(LibraryName)]
    private static extern int FLISetVBin(IntPtr dev, int vbin);

    [DllImport(LibraryName)]
    private static extern int FLISetFrameType(IntPtr dev, FliFrameType frameType);

    [DllImport(LibraryName)]
    private static extern int FLISetExposureTime(IntPtr dev, int exposureMilliseconds);

    [DllImport(LibraryName)]
    private static extern int FLISetTDI(IntPtr dev, int tdiRate, int flags);

    [DllImport(LibraryName)]
    private static extern int FLISetNFlushes(IntPtr dev, int nflushes);

    [DllImport(LibraryName)]
    private static extern int FLIExposeFrame(IntPtr dev);

    [DllImport(LibraryName)]
    private static extern int FLIGetExposureStatus(IntPtr dev, out int timeleft);

    [DllImport(LibraryName)]
    private static extern int FLIGetReadoutDimensions(
        IntPtr dev,
        out int width,
        out int horizontalOffset,
        out int horizontalBin,
        out int height,
        out int verticalOffset,
        out int verticalBin);

    [DllImport(LibraryName)]
    private static extern int FLIGrabRow(IntPtr dev, IntPtr buff, int width);

    [DllImport(LibraryName)]
    private static extern int FLICancelExposure(IntPtr dev);

    [DllImport(LibraryName)]
    private static extern int FLIGetTemperature(IntPtr dev, out double temperature);

    [DllImport(LibraryName)]
    private static extern int FLISetTemperature(IntPtr dev, double temperature);

    [DllImport(LibraryName)]
    private static extern int FLIGetCoolerPower(IntPtr dev, out double power);

    [DllImport(LibraryName)]
    private static extern int FLIControlBackgroundFlush(IntPtr dev, FliBackgroundFlush mode);

    [DllImport(LibraryName)]
    private static extern int FLIGetDeviceStatus(IntPtr dev, out int status);

    [DllImport(LibraryName)]
    private static extern int FLILockDevice(IntPtr dev);

    [DllImport(LibraryName)]
    private static extern int FLIUnlockDevice(IntPtr dev);

    [DllImport(LibraryName)]
    private static extern int FLIGetCameraMode(IntPtr dev, out int modeIndex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int FLIGetCameraModeString(IntPtr dev, int modeIndex, StringBuilder modeString, int len);

    [DllImport(LibraryName)]
    private static extern int FLISetCameraMode(IntPtr dev, int modeIndex);
}

[Flags]
public enum FliDomain
{
    None = 0x00,
    Usb = 0x02,
    Camera = 0x100,
}

public enum FliFrameType
{
    Normal = 0,
    Dark = 1,
    Flood = 2,
}

public enum FliBackgroundFlush
{
    Stop = 0x0000,
    Start = 0x0001,
}

[Flags]
public enum FliDeviceStatus
{
    CameraStatusUnknown = unchecked((int)0xffffffff),
    CameraStatusMask = 0x00000003,
    CameraStatusIdle = 0x00,
    CameraStatusWaitingForTrigger = 0x01,
    CameraStatusExposing = 0x02,
    CameraStatusReadingCcd = 0x03,
    CameraDataReady = unchecked((int)0x80000000),
}
