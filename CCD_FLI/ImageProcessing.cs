using Emgu.CV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TRTUITest
{
    public static class ImageProcessing
    {
        public static void GetUpperAndLower8Bit(Matrix<Byte> CVMat, out Byte LowerValue, out Byte UpperValue, Double LowerPercen, Double UpperPercen)
        {
            int DataLength = CVMat.Rows * CVMat.Cols;
            Byte[] Data = new Byte[DataLength];
            CVMat.Mat.CopyTo(Data);

            int LowerPosition = Convert.ToInt32(DataLength * LowerPercen / 100);
            int UpperPosition = Convert.ToInt32(DataLength * UpperPercen / 100);

            List<Byte> DataList = Data.ToList();
            DataList.Sort();

            LowerValue = DataList[LowerPosition == DataLength ? LowerPosition - 1 : LowerPosition];
            UpperValue = DataList[UpperPosition == DataLength ? UpperPosition - 1 : UpperPosition];
        }

        public static Matrix<Byte> StretchImage8Bit(Matrix<Byte> ByteImage, int MinVal, int MaxVal)
        {
            Byte Devider = Convert.ToByte(Math.Abs((Byte)MaxVal - (Byte)MinVal));
            Devider = Devider <= 0 ? (Byte)1 : Devider;

            Matrix<Byte> NewImg = (((ByteImage - (Byte)MinVal) / Devider * (Byte)255));
            return NewImg;
        }

        public static void GetUpperAndLowerU16Bit(Matrix<UInt16> CVMat, out UInt16 LowerValue, out UInt16 UpperValue, Double LowerPercen, Double UpperPercen)
        {
            int DataLength = CVMat.Rows * CVMat.Cols;
            UInt16[] Data = new UInt16[DataLength];
            CVMat.Mat.CopyTo(Data);

            int LowerPosition = Convert.ToInt32(DataLength * LowerPercen / 100);
            int UpperPosition = Convert.ToInt32(DataLength * UpperPercen / 100);

            List<UInt16> DataList = Data.ToList();
            DataList.Sort();

            LowerValue = DataList[LowerPosition == DataLength ? LowerPosition - 1 : LowerPosition];
            UpperValue = DataList[UpperPosition == DataLength ? UpperPosition - 1 : UpperPosition];
        }

        /*
        public static Matrix<UInt16> StretchImageU16Bit(Matrix<UInt16> UInt16Image, UInt16 MinVal, UInt16 MaxVal)
        {
            UInt16 Devider = Convert.ToUInt16(Math.Abs((UInt16)MaxVal - (UInt16)MinVal));
            Devider = Devider <= 0 ? (UInt16)1 : Devider;

            Matrix<UInt16> NewImg = (((UInt16Image - (UInt16)MinVal) / Devider * (UInt16)65535));
            return NewImg;
        }
        */

        public static void GetUpperAndLower32Bit(Matrix<int> CVMat, out int LowerValue, out int UpperValue, Double LowerPercen, Double UpperPercen)
        {
            int DataLength = CVMat.Rows * CVMat.Cols;
            int[] Data = new int[DataLength];
            CVMat.Mat.CopyTo(Data);

            int LowerPosition = Convert.ToInt32(DataLength * LowerPercen / 100);
            int UpperPosition = Convert.ToInt32(DataLength * UpperPercen / 100);

            List<int> DataList = Data.ToList();
            DataList.Sort();

            LowerValue = DataList[LowerPosition == DataLength ? LowerPosition - 1 : LowerPosition];
            UpperValue = DataList[UpperPosition == DataLength ? UpperPosition - 1 : UpperPosition];
        }

        public static Matrix<int> StretchImage32Bit(Matrix<int> UInt32Image, int MinVal, int MaxVal)
        {
            int Devider = Convert.ToUInt16(Math.Abs((int)MaxVal - (int)MinVal));
            Devider = Devider <= 0 ? (int)1 : Devider;

            Matrix<int> NewImg = (((UInt32Image - (int)MinVal) / Devider * (int)65535));
            return NewImg;
        }

        /*
        public static void GetStrecthProfile(out Double LowerPercen, out Double UpperPercen)
        {
            String StretchProfile = Properties.Settings.Default.ImagingScreenStretchProfile;

            if (StretchProfile == "low")
            {
                UpperPercen = 99.71;
                LowerPercen = 5.6;
            }
            else if (StretchProfile == "medium")
            {
                UpperPercen = 99.93;
                LowerPercen = 0.4;
            }
            else if (StretchProfile == "hight")
            {
                UpperPercen = 99.25;
                LowerPercen = 50.00;
            }
            else if (StretchProfile == "moon")
            {
                UpperPercen = 99.87;
                LowerPercen = 95.04;
            }
            else if (StretchProfile == "planet")
            {
                UpperPercen = 99.92;
                LowerPercen = 99.16;
            }
            else if (StretchProfile == "Max Value")
            {
                UpperPercen = 100.00;
                LowerPercen = 0.0;
            }
            else
            {
                UpperPercen = 100.00;
                LowerPercen = 0.0;
            }
        }
        */

        public static void GetStrecthProfile(out Double LowerPercen, out Double UpperPercen)
        {
            String StretchProfile = "medium";

            if (StretchProfile == "low")
            {
                UpperPercen = 99.71;
                LowerPercen = 5.6;
            }
            else if (StretchProfile == "medium")
            {
                UpperPercen = 85;
                LowerPercen = 80;
            }
            else if (StretchProfile == "hight")
            {
                UpperPercen = 99.25;
                LowerPercen = 50.00;
            }
            else if (StretchProfile == "moon")
            {
                UpperPercen = 99.87;
                LowerPercen = 95.04;
            }
            else if (StretchProfile == "planet")
            {
                UpperPercen = 99.92;
                LowerPercen = 99.16;
            }
            else if (StretchProfile == "Max Value")
            {
                UpperPercen = 100.00;
                LowerPercen = 0.0;
            }
            else
            {
                UpperPercen = 100.00;
                LowerPercen = 0.0;
            }
        }

        public static void GetUpperAndLowerShortBit(Matrix<ushort> CVMat, out ushort LowerValue, out ushort UpperValue, Double LowerPercen, Double UpperPercen)
        {
            int DataLength = CVMat.Rows * CVMat.Cols;
            ushort[] Data = new ushort[DataLength];
            CVMat.Mat.CopyTo(Data);

            int LowerPosition = Convert.ToInt32(DataLength * LowerPercen / 100);
            int UpperPosition = Convert.ToInt32(DataLength * UpperPercen / 100);

            List<ushort> DataList = Data.ToList();
            DataList.Sort();

            LowerValue = DataList[LowerPosition == DataLength ? LowerPosition - 1 : LowerPosition];
            UpperValue = DataList[UpperPosition == DataLength ? UpperPosition - 1 : UpperPosition];
        }

        public static Matrix<UInt16> StretchImageU16Bit(Matrix<UInt16> UInt16Image, UInt16 MinVal, UInt16 MaxVal)
        {
            Double A = (Double)ushort.MinValue;
            Double B = (Double)ushort.MaxValue;
            Double C = (Double)MinVal;
            Double D = (Double)MaxVal;

            Matrix<UInt16> NewImg = (UInt16Image - C) * ((B - A) / (D - C)) + A;

            //Double Devider = Math.Abs((UInt16)MaxVal - (UInt16)MinVal);
            //Devider = Devider <= 0 ? (UInt16)1 : Devider;

            //Matrix<Double> UInt16ImageD = UInt16Image.Convert<Double>();
            ////Matrix<UInt16> NewImg = (((UInt16ImageD - (Double)MinVal) / (Double)Devider) * (Double)ushort.MaxValue).Convert<UInt16>();
            //Matrix<UInt16> NewImg = (((UInt16Image - (Double)MinVal) / Devider) * (Double)ushort.MaxValue);
            return NewImg;
        }
    }
}
