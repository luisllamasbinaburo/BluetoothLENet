using System;

namespace BluetoothLENet.Exceptions
{
    public class DeviceDiscoverException : Exception
    {
        public DeviceDiscoverException() : base("Could not find the specific device.")
        {
        }
    }
}
