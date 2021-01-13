using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LighthouseManagerService
{
    public class AppSettings
    {
        public int Interval { get; set; }
        public string LighthouseManagerPath { get; set; }
        public string BaseStationAddresses { get; set; }
    }
}
