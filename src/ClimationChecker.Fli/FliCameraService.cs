namespace ClimationChecker.Fli;

public sealed class FliCameraService
{
    public IReadOnlyList<FliCameraDescriptor> ListCameras()
    {
        return FliCameraSdk.ListUsbCameras();
    }

    public FliCameraConnection OpenBySerial(string serialNumber)
    {
        var camera = ListCameras().FirstOrDefault(item => item.SerialNumber == serialNumber);
        if (camera is null)
        {
            throw new InvalidOperationException($"No FLI USB camera with serial '{serialNumber}' was found.");
        }

        return new FliCameraConnection(new FliCameraSdk(camera.FileName, FliDomain.Camera | FliDomain.Usb), serialNumber);
    }
}
