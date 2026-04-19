using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace CCD_FLI
{
    public class TRTSetting
    {
        public class Settings
        {
            public string DEVICE_CATELOG { get; set; }
            public double SERVICE_VERSION { get; set; }
            public string SERVICE_EXE { get; set; }
            public string CONFIG_EXE { get; set; }
            public long DEVICE_SERIAL { get; set; }
            public string DEVICE_NAME { get; set; }

            public string SERVER_ADDRESS { get; set; }
            public int SERVER_PORT { get; set; }
            public bool SERVICE_ENABLED { get; set; }
        }

        public Settings DATA = null;
        private string SettingPath = null;

        public TRTSetting()
        {
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            SettingPath = Path.Combine(assemblyFolder, "trt_config.json");
        }

        public bool LoadSetting()
        {
            DATA = new Settings();
            string JsonString = "";

            if (!File.Exists(SettingPath))
            {
                throw new FileNotFoundException(SettingPath);
            }

            try
            {
                using (StreamReader r = new StreamReader(SettingPath))
                {
                    JsonString = r.ReadToEnd();
                }

                if (JsonString != "")
                {
                    DATA = JsonConvert.DeserializeObject<Settings>(JsonString);
                }
                else
                {
                    File.Create(SettingPath).Close();
                    SaveSetting();
                }
            }
            catch
            {
                throw new FileLoadException("Error Loadding Setting");
            }

            return true;
        }

        public void SaveSetting()
        {
            try
            {
                using (StreamWriter sw = new StreamWriter(SettingPath))
                {
                    String DataJson = JsonConvert.SerializeObject(DATA, Formatting.Indented);
                    sw.WriteLine(DataJson);
                    sw.Close();
                }
            }
            catch (Exception)
            {
                Console.WriteLine("Save configulation file error !");
            }
        }
    }
}
