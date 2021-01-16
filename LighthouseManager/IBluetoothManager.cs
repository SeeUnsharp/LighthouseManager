using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LighthouseManager.Helper;

namespace LighthouseManager
{
    public interface IBluetoothManager
    {
        void StartWatcher();

        void StopWatcher();

        Task ChangePowerstate(ulong address, Powerstate powerState);

        Task Identify(ulong address);
    }
}
