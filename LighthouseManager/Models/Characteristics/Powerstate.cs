using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LighthouseManager.Models.Characteristics
{
    public class Powerstate : ICharacteristic
    {
        public const int Wake = 0x01;
        public const int Standby = 0x02;
        public const int Sleep = 0x00;

        public Guid GetGuid()
        {
            return Guid.Parse("00001525-1212-EFDE-1523-785FEABCD124");
        }
    }
}
