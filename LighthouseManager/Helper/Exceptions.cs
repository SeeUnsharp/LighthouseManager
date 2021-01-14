using System;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace LighthouseManager.Helper
{
    public class GattCommunicationException : Exception
    {
        public GattCommunicationException(string message, GattCommunicationStatus status)
            : base(message)
        {
            Status = status;
        }

        public GattCommunicationStatus Status { get; }
    }

    public class BluetoothConnectionException : Exception
    {
        public BluetoothConnectionException(string message)
            : base(message)
        {
            
        }
    }
}