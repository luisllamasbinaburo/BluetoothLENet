
using System.Diagnostics;
using Windows.Devices.Enumeration;

namespace BluetoothLENet_Test
{
    [TestClass()]
    public class BluetoothLENetTests
    {
        [TestMethod()]
        public async Task ScanTest()
        {
            var events = new List<string> ();
            
            var ble = new BluetoothLENet.BLE();
            ble.StartScanning();
            ble.DiscoveredDevice += (s, e) => events.Add("{e.Name}");
            
            await Task.Delay(3000);

            Assert.AreNotEqual(0, events.Count);
        }       

        [TestMethod()]
        public async Task GetListDevicesNamesTest()
        {
            var ble = new BluetoothLENet.BLE();
            ble.StartScanning();

            await Task.Delay(3000);

            var rst = ble.GetListDevicesNames();
            Assert.AreNotEqual(0, rst.Length);
        }

        [TestMethod()]
        public async Task ConnectDeviceTest()
        {
            var ble = new BluetoothLENet.BLE();
            ble.StartScanning();
            
            var deviceName = "Triones-A115220018C9";           
            var rst = await ble.ConnectDevice(deviceName);
            Assert.AreEqual(BluetoothLENet.ConnectDeviceResult.Ok, rst);
        }


        [TestMethod()]
        public async Task FindDeviceByNameTest()
        {
            var ble = new BluetoothLENet.BLE();
            ble.StartScanning();
           
            var device = await ble.FindDeviceByName("Triones-A115220018C9");
            Assert.IsNotNull(device);
        }

        [TestMethod()]
        public async Task WriteCharacteristicTest()
        {
            var ble = new BluetoothLENet.BLE();
            ble.StartScanning();           

            var deviceName = "Triones-A115220018C9";
            var rst = await ble.ConnectDevice(deviceName);

            await ble.WriteCharacteristic("65493", "65497", "cc 24 33");

            ble.CloseDevice();
        }
    }
}