using FliSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static FliSharp.FLI;

namespace CCD_FLI
{
    public class DeviceInfo
    {
        public string FileName;
        public string ModelName;
        public string SerialNumber;
    }

    public class CCD_MODULE
    {
        private FLI fli = null;
        public string Brand = null;

        public int TDIRate = 0; //8Mhz
        public int HBin = 1;
        public int VBin = 1;

        public short XSub = 1;
        public short YSub = 1;

        public bool isCoolerOn = false;

        private double setTemp = -20;

        public CCD_MODULE()
        {
            TDIRate = 0; //8Mhz
            HBin = 1;
            VBin = 1;

            XSub = 1;
            YSub = 1;

            isCoolerOn = false;
            Brand = "FLI";
        }

        public DeviceInfo[] GetList()
        {
            DeviceName[] names;

            names = FLI.List(FLI.DOMAIN.CAMERA | FLI.DOMAIN.USB);

            if (names.Count() > 0)
            {
                DeviceInfo[] info = new DeviceInfo[names.Count()];

                int i = 0;

                foreach (DeviceName name in names)
                {
                    info[i] = new DeviceInfo();
                    info[i].FileName = name.FileName;
                    info[i].ModelName = name.ModelName;

                    FLI fli = new FLI(name.FileName, FLI.DOMAIN.CAMERA | FLI.DOMAIN.USB);
                    name.SerialNumber = fli.GetSerialString();

                    info[i].SerialNumber = name.SerialNumber;

                    ++i;
                }

                return info;
            }
            else
            {
                return null;
            }
        }

        public bool Connect(string serialNo)
        {
            DeviceName[] names;

            names = FLI.List(FLI.DOMAIN.CAMERA | FLI.DOMAIN.USB);

            DeviceInfo[] info = null;

            if (names.Count() > 0)
            {
                info = new DeviceInfo[names.Count()];

                int i = 0;

                foreach (DeviceName name in names)
                {
                    info[i] = new DeviceInfo();
                    info[i].FileName = name.FileName;
                    info[i].ModelName = name.ModelName;

                    FLI fli = new FLI(name.FileName, FLI.DOMAIN.CAMERA | FLI.DOMAIN.USB);
                    name.SerialNumber = fli.GetSerialString();

                    info[i].SerialNumber = name.SerialNumber;

                    ++i;
                }
            }

            if (info == null) throw new Exception("Not found CCD, Exception");
            if (info.Length <= 0) throw new Exception("Not found CCD, Exception");

            for (int i = 0; i < info.Count(); ++i)
            {
                if (info[i].SerialNumber == serialNo)
                {
                    fli = new FLI(info[i].FileName, FLI.DOMAIN.CAMERA | FLI.DOMAIN.USB);
                    return true;
                }
            }
            
            return false;
        }

        public bool isConnect()
        {
            if (fli == null) return false;
            return true;
        }

        public string GetBrand()
        {
            if (fli == null) return null;
            return Brand;
        }

        public string GetModel()
        {
            if (fli == null) return null;
            return fli.GetModel();
        }

        public string GetSerialString()
        {
            if (fli == null) return null;

            return fli.GetSerialString();
        }

        public string GetDeviceStatus()
        {
            if (fli == null) return null;

            try
            {
                STATUS status = fli.GetDeviceStatus();

                //Console.WriteLine((STATUS)status.ToString());
                if (status.HasFlag(STATUS.CAMERA_STATUS_UNKNOWN))
                {
                    return "Unknow";
                }
                else if (status.HasFlag(STATUS.CAMERA_STATUS_READING_CCD))
                {
                    return "Reading";
                }
                else if (status.HasFlag(STATUS.CAMERA_STATUS_EXPOSING))
                {
                    return "Exposing";
                }               
                else if (status.HasFlag(STATUS.CAMERA_STATUS_MASK))
                {
                    return "Mask";
                }
                else if (status.HasFlag(STATUS.CAMERA_STATUS_WAITING_FOR_TRIGGER))
                {
                    return "Wait Trigger";
                }
                else if (status.HasFlag(STATUS.CAMERA_STATUS_IDLE))
                {
                    return "Idle";
                }
                else
                {
                    return null;
                }
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public bool IsShutterOpen()
        {
            return (this.GetDeviceStatus() == "Exposing" ? true : false);
        }

        public bool CanSetTemp()
        {
            return true;
        }

        public double GetCoolerPower()
        {
            if (fli != null)
            {
                return fli.GetCoolerPower();
            }
            else
            {
                return -1;
            }
        }

        public bool GetFastReadOutMode()
        {
            if (TDIRate == 8000000)
            {
                return false;
            }

            return false;
        }

        public int GetReadOutMode()
        {
            return TDIRate;
        }

        public bool HasShutter()
        {
            return true;
        }

        public double GetTemperature()
        {
            return fli.GetTemperature();
        }

        public void FLIGetArrayArea(out int ul_x, out int ul_y, out int lr_x, out int lr_y)
        {
            fli.GetArrayArea(out ul_x, out ul_y, out lr_x, out lr_y);
        }

        public void GetVisibleArea(out int ul_x, out int ul_y, out int lr_x, out int lr_y)
        {
            fli.GetVisibleArea(out ul_x, out ul_y, out lr_x, out lr_y);
        }

        public void SetImageArea(int ul_x, int ul_y, int lr_x, int lr_y)
        {
            fli.SetImageArea(ul_x, ul_y, lr_x, lr_y);
        }

        public void SetFrameType(FRAME_TYPE frameType)
        {            
            fli.SetFrameType(frameType);
        }

        public void SetHBin(int HBin)
        {
            fli.SetHBin(HBin);
            this.HBin = HBin;
        }

        public void SetVBin(int VBin)
        {
            fli.SetVBin(VBin);
            this.VBin = VBin;
        }

        public void SetTDI(int TDIRate)
        {
            fli.SetTDI(TDIRate);
            this.TDIRate = TDIRate;
        }

        public void SetExposureTime(int expTime)
        {
            fli.SetExposureTime(expTime);
        }

        public void Expose()
        {
            fli.ExposeFrame();
        }

        public bool IsDownloadReady()
        {
            return fli.IsDownloadReady();
        }

        public bool IsDownloadReady(out int timeLeft)
        {
            return fli.IsDownloadReady(out timeLeft);
        }

        public void GrabRow(ushort[] buff)
        {
            fli.GrabRow(buff);
        }

        public void SetTemperature(double temp)
        {
            this.setTemp = temp;
            fli.SetTemperature(this.setTemp);
        }

        public void SetCoolerOn(bool isCoolerOn)
        {
            if (isCoolerOn)
            {
                this.isCoolerOn = true;
            }
            else
            {
                this.isCoolerOn = false;
            }
        }

        public string GetCameraModeString(int mode)
        {
            return fli.GetCameraModeString(mode);
        }

        public int GetCameraMode()
        {
            return fli.GetCameraMode();
        }

        public void SetCameraMode(int mode)
        {
            fli.SetCameraMode(mode);
        }

        public void SetNFlushes(int flushTime)
        {
            fli.SetNFlushes(flushTime);
        }


        public bool Lock()
        {
            fli.LockDevice();
            return true;
        }

        public bool Unlock()
        {
            fli.UnlockDevice();
            return true;
        }


        public void CancelExposure()
        {
            fli.CancelExposure();
        }

        public void SetBackgroundFlush(BGFLUSH flushMode)
        {
            fli.ControlBackgroundFlush(flushMode);
        }

        public void Close()
        {
            if (fli != null)
            {
                fli.Close();
            }
        }
    }
}
