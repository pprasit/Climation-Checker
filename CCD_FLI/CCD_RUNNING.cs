using DEVICE_PACKET;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Lidgren.Network;
using Nancy.Hosting.Self;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using TRTQueue;
using TRTUITest;

namespace CCD_FLI
{
    public class CCD_RUNNING : ApplicationContext
    {
        private NetClient s_client = null;
        private string DEVICE_NAME = null;
        private long DEVICE_SERIAL = 0;
        private double SERVICE_VERSION = 0;

        private string SERVER_ADDRESS = null;
        private int SERVER_PORT = 0;

        private string serialNo = "";
        private int M3 = 0;

        private CCD_MODULE fli = null;
        private CCDInfo ccdInfo = new CCDInfo();

        private System.Timers.Timer timer = new System.Timers.Timer();
        private System.Timers.Timer timerAndor = new System.Timers.Timer();

        private bool isExpose = false;

        private string hostIP = "";
        private int hostPort = 0;
        private string currentPath;

        private bool isFLIConnect = false;

        private double setTemp = 20;

        private Dictionary<int, string> ccdMode = new Dictionary<int, string>();

        public CCD_RUNNING(string serialNumber, int M3, string hostIP, int hostPort)
        {
            TRTSetting trtSetting = new TRTSetting();
            trtSetting.LoadSetting();
            DEVICE_NAME = trtSetting.DATA.DEVICE_NAME;
            DEVICE_SERIAL = trtSetting.DATA.DEVICE_SERIAL;
            SERVER_ADDRESS = trtSetting.DATA.SERVER_ADDRESS;
            SERVER_PORT = trtSetting.DATA.SERVER_PORT;
            SERVICE_VERSION = CCD_INFO.version;

            serialNo = serialNumber;
            this.M3 = M3;

            this.hostIP = hostIP;
            this.hostPort = hostPort;

            this.currentPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

            if (!Directory.Exists(currentPath + @"\CCD_TEMP"))
            {
                Directory.CreateDirectory(currentPath + @"\CCD_TEMP");
            }

            if (!Directory.Exists(currentPath + @"\CCD_FITS"))
            {
                Directory.CreateDirectory(currentPath + @"\CCD_FITS");
            }            

            var config = new HostConfiguration
            {
                RewriteLocalhost = true,
                UrlReservations = new UrlReservations { CreateAutomatically = true }
            };

            var host = new NancyHost(new Uri("http://localhost:" + hostPort), new NancyHostStartup(), config);
            host.Start();

            timerAndor.Enabled = false;
            timerAndor.Interval = 100;
            timerAndor.Elapsed += getStatus;

            connectService();
        }

        public void connectService()
        {
            NetPeerConfiguration config = new NetPeerConfiguration("TRT");
            config.ConnectionTimeout = 5;
            s_client = new NetClient(config);
            s_client.Start();
            s_client.Connect(SERVER_ADDRESS, SERVER_PORT);
            s_client.RegisterReceivedCallback(new SendOrPostCallback(receivingMsg), new SynchronizationContext());
        }

        private void receivingMsg(object state)
        {
            NetIncomingMessage im;
            while ((im = s_client.ReadMessage()) != null)
            {
                // handle incoming message
                switch (im.MessageType)
                {
                    case NetIncomingMessageType.StatusChanged:
                        NetConnectionStatus status = (NetConnectionStatus)im.ReadByte();
                        string reason = im.ReadString();

                        if (status == NetConnectionStatus.Connected)
                        {
                            NetOutgoingMessage om = PacketParse.CreateConnectPacket(im.SenderConnection.Peer, DEVICE_CATELOG.TRT_CLIENT, DEVICE_SERIAL, (ushort)DEVICE_CATELOG.CCD, TimeUtil.getDateTime().Ticks, (ushort)TRT_PACKET.CONNECT_DEVICE, SERVICE_VERSION, DEVICE_NAME);
                            s_client.SendMessage(om, NetDeliveryMethod.ReliableOrdered, 1);
                        }
                        else if (status == NetConnectionStatus.Disconnected)
                        {
                            NetOutgoingMessage om = PacketParse.CreateConnectPacket(im.SenderConnection.Peer, DEVICE_CATELOG.TRT_CLIENT, DEVICE_SERIAL, (ushort)DEVICE_CATELOG.CCD, TimeUtil.getDateTime().Ticks, (ushort)TRT_PACKET.DISCONNECT_DEVICE, SERVICE_VERSION, DEVICE_NAME);
                            s_client.SendMessage(om, NetDeliveryMethod.ReliableOrdered, 1);
                            closeCCD();
                        }

                        break;
                    case NetIncomingMessageType.Data:

                        DEVICE_CATELOG deviceType = (DEVICE_CATELOG)im.ReadByte();

                        if (deviceType == DEVICE_CATELOG.TIMESYNCING)
                        {
                            TimeUtil.syncTime(im.ReadInt64(), im.ReadBoolean());
                            return;
                        }

                        long deviceCode = im.ReadInt64();
                        CCD_PACKET packetType = (CCD_PACKET)im.ReadUInt16();
                        long dateTimeStamp = im.ReadInt64();
                        int packetLength = im.ReadInt32();

                        if (deviceType == DEVICE_CATELOG.CCD)
                        {
                            if (packetType == CCD_PACKET.DAEMON_INIT)
                            {
                                if (fli == null)
                                {
                                    fli = new CCD_MODULE();
                                    if (fli.Connect(serialNo))
                                    {
                                        int i = 0;
                                        while (true)
                                        {
                                            string cameraModeStr = fli.GetCameraModeString(i);
                                            if (cameraModeStr != null)
                                            {
                                                ccdMode.Add(i++, cameraModeStr);
                                            }
                                            else
                                            {
                                                break;
                                            }
                                        }

                                        fli.CancelExposure();
                                        fli.SetBackgroundFlush(FliSharp.FLI.BGFLUSH.START);

                                        isFLIConnect = true;
                                        Console.WriteLine("CCD Connected");
                                        timerAndor.Start();
                                    }
                                }
                            }
                            else if (packetType == CCD_PACKET.REMOVE_TEMP)
                            {
                                string value = PacketParse.GetString(im);

                                Task t = Task.Run(async () =>
                                {
                                    while (true)
                                    {
                                        try
                                        {

                                            if (File.Exists(currentPath + @"\CCD_TEMP\" + value + ".raw"))
                                            {
                                                File.Delete(currentPath + @"\CCD_TEMP\" + value + ".raw");
                                            }

                                            if (File.Exists(currentPath + @"\CCD_TEMP\" + value + ".info"))
                                            {
                                                File.Delete(currentPath + @"\CCD_TEMP\" + value + ".info");
                                            }

                                            break;
                                        }
                                        catch
                                        {

                                        }

                                        await Task.Delay(1000);
                                    }

                                    Console.WriteLine("CCD Deleted: " + value);
                                });
                            }
                            else
                            {
                                CCD_COMMAND CCDCMD = (CCD_COMMAND)im.ReadUInt16();
                                string value = PacketParse.GetString(im);

                                if (packetType == CCD_PACKET.SET_CMD)
                                {
                                    if (CCDCMD == CCD_COMMAND.CONNECT)
                                    {
                                        if (fli.Connect(this.serialNo))
                                        {
                                            isFLIConnect = true;
                                            //timerAndor.Start();
                                        }
                                    }                                    
                                    else if (CCDCMD == CCD_COMMAND.SETEXPOSUREINFO)
                                    {
                                        if (fli.isConnect())
                                        {
                                            string[] exposureInfo = value.Split(','); //0=HBIN,2=VBIN,3=SUBFRAME_X,4=SUBFRAME_Y

                                            if (exposureInfo[0] != "N") fli.SetHBin(int.Parse(exposureInfo[0]));
                                            if (exposureInfo[1] != "N") fli.SetVBin(int.Parse(exposureInfo[1]));
                                            if (exposureInfo[2] != "N") fli.XSub = short.Parse(exposureInfo[2]);
                                            if (exposureInfo[3] != "N") fli.YSub = short.Parse(exposureInfo[3]);

                                            Console.WriteLine("Exposure Info has been set to: " + value);
                                        }
                                    }
                                    else if (CCDCMD == CCD_COMMAND.EXPOSE)
                                    {
                                        if (!isExpose)
                                        {
                                            if (fli.isConnect())
                                            {
                                                Task t = Task.Run(() =>
                                                {
                                                    fli.CancelExposure();
                                                    
                                                    ccdInfo.currentExposeTick = 0;
                                                    ccdInfo.statusNote = null;

                                                    string[] exposureInfo = value.Split(',');

                                                    if (exposureInfo[0] == "DARK")
                                                    {
                                                        fli.SetFrameType(FliSharp.FLI.FRAME_TYPE.DARK);
                                                    }
                                                    else
                                                    {
                                                        fli.SetFrameType(FliSharp.FLI.FRAME_TYPE.NORMAL);
                                                    }                                                                                                   

                                                    int ul_x, ul_y, lr_x, lr_y;
                                                    fli.GetVisibleArea(out ul_x, out ul_y, out lr_x, out lr_y);

                                                    int width = lr_x - ul_x;
                                                    int height = lr_y - ul_y;

                                                    if (fli.XSub > 1 || fli.YSub > 1)
                                                    {
                                                        int centerX = (width - ul_x) / fli.XSub;
                                                        int centerY = (height - ul_y) / fli.YSub;

                                                        fli.SetImageArea((centerX / fli.XSub), (centerY / fli.YSub), (centerX * fli.XSub), (centerY * fli.YSub));
                                                    }
                                                    else
                                                    {
                                                        fli.SetImageArea(ul_x, ul_y, lr_x, lr_y);
                                                    }

                                                    double exposeTime = double.Parse(exposureInfo[1]) * 1000;
                                                    fli.SetExposureTime((int)(exposeTime));
                                                    fli.SetTDI(0);

                                                    fli.Expose();
                                                    isExpose = true;

                                                    Console.WriteLine("EXPOSING");
                                                    int timeLeft = 0;

                                                    while (!fli.IsDownloadReady(out timeLeft))
                                                    {
                                                        ccdInfo.statusNote = "Exposing -> " + Math.Round(timeLeft/1000.0, 2) + "s left";
                                                        Task.Delay(200);
                                                    }

                                                    ccdInfo.currentExposeTick = dateTimeStamp;

                                                    Console.WriteLine("DOWNLOAD....");

                                                    int fullWidth = width;
                                                    int fullHeight = height;

                                                    if (fli.XSub > 1 || fli.YSub > 1 || fli.HBin > 1 || fli.VBin > 1)
                                                    {
                                                        fullWidth = width;
                                                        fullHeight = height;
                                                        width = (width / fli.XSub / fli.HBin);
                                                        height = (height / fli.YSub / fli.VBin);
                                                    }

                                                    ushort[][] dataGrabFLI = new ushort[fullHeight][];

                                                    FitsInfo fitsInfo = new FitsInfo
                                                    {
                                                        width = width,
                                                        height = height
                                                    };

                                                    File.WriteAllText(currentPath + @"\CCD_TEMP\" + exposureInfo[2] + ".info", JsonConvert.SerializeObject(fitsInfo));

                                                    using (FileStream fs = new FileStream(currentPath + @"\CCD_TEMP\" + exposureInfo[2] + ".raw", FileMode.OpenOrCreate, FileAccess.Write))
                                                    {
                                                        using (BinaryWriter bw = new BinaryWriter(fs))
                                                        {
                                                            for (int y = 0; y < height; y++)
                                                            {
                                                                dataGrabFLI[y] = new ushort[fullWidth];
                                                                fli.GrabRow(dataGrabFLI[y]);

                                                                for (int x = 0; x < width; x++)
                                                                {
                                                                    //bw.Write(dataGrabFLI[y][x]);
                                                                    bw.Write((short)(dataGrabFLI[y][x] - short.MinValue));
                                                                }
                                                            }
                                                        }
                                                    }

                                                    isExpose = false;
                                                    sendPacketToServer(CCD_PACKET.CCD_RAW_IMAGE, @"CCD_TEMP/" + exposureInfo[2] + ".raw," + dateTimeStamp);

                                                    //using (Matrix<ushort> imageMatrixOriginal = new Matrix<ushort>(width, height))
                                                    //{                                                        
                                                    //    using (FileStream fs = new FileStream(currentPath + @"CCD_TEMP\" + exposureInfo[2] + ".raw", FileMode.OpenOrCreate, FileAccess.Write))
                                                    //    {                                                            
                                                    //        using (BinaryWriter bw = new BinaryWriter(fs))
                                                    //        {
                                                    //            for (int y = 0; y < height; y++)
                                                    //            {
                                                    //                dataGrabFLI[y] = new ushort[width];
                                                    //                fli.GrabRow(dataGrabFLI[y]);

                                                    //                for (int x = 0; x < width; x++)
                                                    //                {
                                                    //                    bw.Write((short)dataGrabFLI[y][x]);
                                                    //                    imageMatrixOriginal.Data[y, x] = dataGrabFLI[y][x];
                                                    //                }
                                                    //            }
                                                    //        }
                                                    //    }

                                                    //    sendPacketToServer(CCD_PACKET.CCD_RAW_IMAGE, @"CCD_TEMP/" + exposureInfo[2] + ".raw," + dateTimeStamp);

                                                    //    //ushort LowerValue, UpperValue;
                                                    //    //double LowerPercen, UpperPercen;
                                                    //    //ImageProcessing.GetStrecthProfile(out LowerPercen, out UpperPercen);
                                                    //    //ImageProcessing.GetUpperAndLowerShortBit(imageMatrixOriginal, out LowerValue, out UpperValue, LowerPercen, UpperPercen);

                                                    //    //string extension = ".png";

                                                    //    //Matrix<ushort> imgJPG = ImageProcessing.StretchImageU16Bit(imageMatrixOriginal, LowerValue, UpperValue);                                                        
                                                    //    //Image<Bgr, Byte> emguImg = imgJPG.Mat.ToImage<Bgr, byte>();

                                                    //    //if (emguImg.Size.Width > 2048)
                                                    //    //{
                                                    //    //    emguImg = emguImg.Resize(0.25, Inter.Linear);
                                                    //    //}
                                                    //    //else
                                                    //    //{
                                                    //    //    emguImg = emguImg.Resize(0.5, Inter.Linear);
                                                    //    //}

                                                    //    //Save(emguImg, currentPath + @"\CCD_FITS\" + exposureInfo[2] + extension, 30);

                                                    //    //emguImg.Dispose();
                                                    //    //imgJPG.Dispose();

                                                    //    //sendPacketToServer(CCD_PACKET.CCD_IMAGE, @"CCD_FITS/" + exposureInfo[2] + extension);
                                                    //}

                                                    Console.WriteLine("GENERATED.... ");
                                                });                                                
                                            }
                                        }
                                    }
                                    else if (CCDCMD == CCD_COMMAND.SETTEMP)
                                    {
                                        if (fli.isConnect())
                                        {
                                            setTemp = double.Parse(value);
                                        }
                                    }
                                    else if (CCDCMD == CCD_COMMAND.SET_MODE) //Readout Mode
                                    {
                                        if (fli.isConnect())
                                        {
                                            int mode = int.Parse(value);
                                            fli.SetCameraMode(mode);

                                            Task t = Task.Run(async () =>
                                            {
                                                await Task.Delay(1000);
                                            });

                                            fli.SetFrameType(FliSharp.FLI.FRAME_TYPE.NORMAL);
                                            int ul_x, ul_y, lr_x, lr_y;
                                            fli.GetVisibleArea(out ul_x, out ul_y, out lr_x, out lr_y);
                                            fli.SetImageArea(ul_x, ul_y, lr_x, lr_y);

                                            int width = lr_x - ul_x;
                                            int height = lr_y - ul_y;

                                            int fullWidth = width;
                                            int fullHeight = height;

                                            ushort[][] dataGrabFLI = new ushort[fullHeight][];
                                            
                                            fli.SetExposureTime((int)0);
                                            fli.SetTDI(0); //reset first image bug

                                            fli.Expose();
                                            isExpose = true;

                                            int timeLeft = 0;

                                            while (!fli.IsDownloadReady(out timeLeft))
                                            {
                                                Task.Delay(200);
                                            }

                                            for (int y = 0; y < height; y++)
                                            {
                                                dataGrabFLI[y] = new ushort[fullWidth];
                                                fli.GrabRow(dataGrabFLI[y]);
                                            }

                                            ccdInfo.currentExposeTick = dateTimeStamp;

                                            isExpose = false;
                                        }
                                    }
                                    else if (CCDCMD == CCD_COMMAND.SETCOOLING)
                                    {
                                        if (fli.isConnect())
                                        {
                                            bool isCooling = bool.Parse(value);

                                            fli.SetCoolerOn(isCooling);
                                            if(!isCooling)
                                            {
                                                fli.SetTemperature(45); //maximum temp fli can do!, There is no set cooler off
                                            }
                                            else
                                            {
                                                fli.SetTemperature(setTemp);
                                            }

                                            Console.WriteLine("SET COOLING STATUS = " + bool.Parse(value) + " with temp: " + setTemp);
                                        }
                                    }
                                }
                            }
                        }

                        break;
                    default:
                        break;
                }
                s_client.Recycle(im);
            }
        }

        public static void Save(Image<Bgr, Byte> img, string filename, double quality)
        {
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality,
                (long)quality
                );

            var pngCodec = (from codec in ImageCodecInfo.GetImageEncoders()
                            where codec.MimeType == "image/png"
                            select codec).Single();

            
            img.AsBitmap().Save(filename, pngCodec, encoderParams);
        }

        private void getStatus(object sender, ElapsedEventArgs e)
        {
            timerAndor.Stop();

            if (fli != null)
            {
                try
                {
                    if (fli.isConnect())
                    {
                        ccdInfo.currentTemp = fli.GetTemperature();
                        ccdInfo.currrentStatus = fli.GetDeviceStatus();
                        ccdInfo.currrentCoolingStatus = (fli.isCoolerOn ? 1 : 0);
                        ccdInfo.currrentCoolingPower = fli.GetCoolerPower();
                        ccdInfo.currentBinningX = fli.HBin;
                        ccdInfo.currentBinningY = fli.VBin;
                        ccdInfo.currentSubframeX = fli.XSub;
                        ccdInfo.currentSubframeY = fli.YSub;
                        ccdInfo.M3Port = M3;
                        ccdInfo.ccdBrand = fli.Brand;
                        ccdInfo.ccdModel = fli.GetModel();
                        ccdInfo.hostIP = this.hostIP;
                        ccdInfo.hostPort = this.hostPort;
                        ccdInfo.CCDModes = JsonConvert.SerializeObject(ccdMode);
                        ccdInfo.currentCCDMode = fli.GetCameraMode().ToString();

                        int ul_x, ul_y, lr_x, lr_y;

                        fli.GetVisibleArea(out ul_x, out ul_y, out lr_x, out lr_y);

                        int width = lr_x - ul_x;
                        int height = lr_y - ul_y;

                        ccdInfo.currentResolutionHeight = height;
                        ccdInfo.currentResolutionWidth = width;

                        sendPacketToServer(CCD_PACKET.CCD_DATA, ccdInfo);
                    }
                    else
                    {
                        ccdInfo.currentTemp = -999;
                        ccdInfo.currrentStatus = "Disconnect";
                        ccdInfo.currrentCoolingStatus = 0;
                        ccdInfo.currrentCoolingPower = 0;
                        ccdInfo.currentBinningX = 1;
                        ccdInfo.currentBinningY = 1;
                        ccdInfo.M3Port = M3;
                        ccdInfo.ccdBrand = "";
                        ccdInfo.ccdModel = "";
                        ccdInfo.hostIP = this.hostIP;
                        ccdInfo.hostPort = this.hostPort;
                        ccdInfo.CCDModes = null;
                        ccdInfo.currentCCDMode = null;
                        ccdInfo.currentExposeTick = 0;
                        ccdInfo.statusNote = null;
                        sendPacketToServer(CCD_PACKET.CCD_DATA, ccdInfo);
                    }
                }
                catch
                {
                    Console.WriteLine("ERROR CCD");
                }

                if (fli.isConnect())
                    timerAndor.Start();
            }
        }

        private void sendPacketToServer(CCD_PACKET Packet, CCDInfo value)
        {
            if (s_client != null && s_client.ConnectionStatus == NetConnectionStatus.Connected)
            {
                NetOutgoingMessage om = PacketParse.CreatePacket(s_client, DEVICE_CATELOG.CCD, DEVICE_SERIAL, (ushort)Packet, TimeUtil.getDateTime().Ticks, value);
                s_client.SendMessage(om, NetDeliveryMethod.ReliableOrdered, 1);
            }
            else if (s_client != null)
            {
                if(s_client.ConnectionStatus == NetConnectionStatus.Disconnected)
                {
                    s_client.Connect(SERVER_ADDRESS, SERVER_PORT);
                }
            }
        }

        private void sendPacketToServer(CCD_PACKET Packet, string value)
        {
            if (s_client != null && s_client.ConnectionStatus == NetConnectionStatus.Connected)
            {
                NetOutgoingMessage om = PacketParse.CreatePacket(s_client, DEVICE_CATELOG.CCD, DEVICE_SERIAL, (ushort)Packet, TimeUtil.getDateTime().Ticks, value);
                s_client.SendMessage(om, NetDeliveryMethod.ReliableOrdered, 1);
            }
            else if (s_client != null)
            {
                if (s_client.ConnectionStatus == NetConnectionStatus.Disconnected)
                {
                    s_client.Connect(SERVER_ADDRESS, SERVER_PORT);
                }
            }
        }

        public void closeCCD()
        {
            fli.Unlock();
            fli.Close();
            fli = null;
        }
    }
}
