using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LighthouseManager.Models.Characteristics
{
    public class Identify : ICharacteristic
    {
        public byte Identifing = 1;

        public Guid GetGuid()
        {
            return Guid.Parse("00008421-1212-EFDE-1523-785FEABCD124");
        }
    }
}
