using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using System.Reflection;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO.Ports;
using System.Diagnostics;
using BluetoothLENet.Classes;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Foundation;

namespace BluetoothLENet
{
    public partial class BLE
    {
        // "Magic" string for all BLE devices
        public readonly string _aqsAllBLEDevices = "(System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";
        public readonly string[] _requestedBLEProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.Bluetooth.Le.IsConnectable", };

        private List<DeviceInformation> _deviceList = new List<DeviceInformation>();
        private BluetoothLEDevice _selectedDevice = null;

        private List<BluetoothLEAttributeDisplay> _services = new List<BluetoothLEAttributeDisplay>();
        private BluetoothLEAttributeDisplay _selectedService = null;

        private List<BluetoothLEAttributeDisplay> _characteristics = new List<BluetoothLEAttributeDisplay>();
        private BluetoothLEAttributeDisplay _selectedCharacteristic = null;

        // Only one registered characteristic at a time.
        private List<GattCharacteristic> _subscribers = new List<GattCharacteristic>();

        // Current data format
        private DataFormat _dataFormat = DataFormat.Hex;

        private ManualResetEvent _notifyCompleteEvent = null;
        private ManualResetEvent _delayEvent = null;
        private bool _primed = false;

        private TimeSpan _timeout = TimeSpan.FromSeconds(3);

        public event TypedEventHandler<DeviceWatcher, DeviceInformation> DiscoveredDevice;
        public event TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> Updated;
        public event TypedEventHandler<DeviceWatcher, object> EnumerationCompleted;

        DeviceWatcher watcher;
        public void StartScanning()
        {
            DeviceWatcher watcher = DeviceInformation.CreateWatcher(_aqsAllBLEDevices, _requestedBLEProperties, DeviceInformationKind.AssociationEndpoint);
            InitWatcherEvents(watcher);

            watcher.Start();
        }

        private void InitWatcherEvents(DeviceWatcher watcher)
        {
            watcher.Added += (DeviceWatcher sender, DeviceInformation devInfo) =>
            {
                DiscoveredDevice?.Invoke(sender, devInfo);
                var added = _deviceList.FirstOrDefault(d => d.Id.Equals(devInfo.Id) || d.Name.Equals(devInfo.Name));
                if (added == null) _deviceList.Add(devInfo);
            };

            // We need handler for this event mandatory, even if empty!
            watcher.Updated += (sender, arg) =>
            {
                Updated?.Invoke(sender, arg);
            }; 

            watcher.Removed += (DeviceWatcher sender, DeviceInformationUpdate devInfo) =>
            {
                var removed = _deviceList.FirstOrDefault(d => d.Id == devInfo.Id);
                if (removed != null) _deviceList.Remove(removed);
            };

            watcher.EnumerationCompleted += (DeviceWatcher sender, object arg) =>
            {
                sender.Stop();
                EnumerationCompleted?.Invoke(sender, arg);
            };

            watcher.Stopped += (DeviceWatcher sender, object arg) =>
            {
                _deviceList.Clear();
                sender.Start();
            };
        }

        public void StopScanning()
        {
            watcher.Stop();
        }

        public async void PairBluetooth()
        {
            DevicePairingResult result = null;
            DeviceInformationPairing pairingInformation = _selectedDevice.DeviceInformation.Pairing;

            await _selectedDevice.DeviceInformation.Pairing.UnpairAsync();

            if (pairingInformation.CanPair)
                result = await _selectedDevice.DeviceInformation.Pairing.PairAsync(pairingInformation.ProtectionLevel);
        }

        public void SetDisplayFormat(DataFormat formatparam)
        {
            _dataFormat = formatparam;
            Debug.WriteLine($"Current display format: {_dataFormat.ToString()}");
        }

        public void Delay(uint milliseconds)
        {
            _delayEvent = new ManualResetEvent(false);
            _delayEvent.WaitOne((int)milliseconds, true);
            _delayEvent = null;
        }

        public TimeSpan SetTimeout(uint seconds)
        {
            if (seconds > 0 && seconds < 60)
            {
                _timeout = TimeSpan.FromSeconds(seconds);
            }

            Debug.WriteLine($"Device connection timeout (sec): {_timeout.TotalSeconds}");
            return _timeout;
        }

        /// <summary>
        /// Get array of available BLE devices
        /// </summary>
        public DeviceInformation[] GetDiscoveredDevices() => _deviceList.ToArray();

        /// <summary>
        /// Get name array of available BLE devices
        /// </summary>
        public string[] GetListDevicesNames()
        {
            var names = _deviceList.OrderBy(d => d.Name).Where(d => !string.IsNullOrEmpty(d.Name)).Select(d => d.Name).ToArray();

            for (int i = 0; i < names.Count(); i++)
                Debug.WriteLine($"#{i:00}: {names[i]}");

            return names;
        }

        public async Task<DeviceInformation> FindDeviceById(string deviceId)
        {
            var action = () =>
            {
                while (true)
                {
                    foreach (var device in GetDiscoveredDevices())
                    {
                        if (device.Id == deviceId)
                        {
                            return device;
                        }
                    }
                }
            };

            return await action.AsTask().TimeoutAfter(_timeout); ;
        }

        public async Task<DeviceInformation> FindDeviceByName(string deviceName)
        {
            var function = () =>
            {
                while (true)
                {
                    foreach (var device in GetDiscoveredDevices())
                    {
                        if (device.Name == deviceName)
                        {
                            return device;
                        }
                    }
                }
            };
            return await function.AsTask().TimeoutAfter(_timeout); ;
        }


        public DeviceState GetStatus() => _selectedDevice == null ?
                                             DeviceState.No_Connected
                                           : _selectedDevice.ConnectionStatus == BluetoothConnectionStatus.Connected ?
                                                  DeviceState.Connected
                                                : DeviceState.Disconnected;
        public void GetCurrentServices() => _services.ToArray();

        public void GetCharacteristics() => _characteristics.ToArray();


        /// <summary>
        /// Connect to the specific device by name or number, and make this device current
        /// </summary>
        /// <param name="deviceName"></param>
        /// <returns></returns>
        public async Task<ConnectDeviceResult> ConnectDevice(string deviceName)
        {
            if (_selectedDevice != null)
                CloseDevice();

            ConnectDeviceResult result;
            if (string.IsNullOrEmpty(deviceName))
            {
                Debug.WriteLine("Device name can not be empty.");
                result = ConnectDeviceResult.Invalid_Name;
            }
            else
            {
                try
                {
                    var device = await FindDeviceByName(deviceName);
                    string deviceId = device.Id;
                    try
                    {
                        _selectedDevice = await BluetoothLEDevice.FromIdAsync(deviceId).AsTask().TimeoutAfter(_timeout);
                        Debug.WriteLine($"Connecting to {_selectedDevice.Name}.");
                        var getDeviceResult = await GetDeviceServices(deviceName);
                        result = getDeviceResult == GattCommunicationStatus.Success ? ConnectDeviceResult.Ok : ConnectDeviceResult.Unreachable;
                    }
                    catch
                    {
                        Debug.WriteLine($"Error accesing to {deviceName}.");
                        result = ConnectDeviceResult.Error;
                    }
                }
                catch
                {
                    Debug.WriteLine("Device not found.");
                    result = ConnectDeviceResult.Not_Found;
                }
            }
    
            return result;
        }

        private async Task<GattCommunicationStatus> GetDeviceServices(string deviceName)
        {
            var result = await _selectedDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

            if (result.Status == GattCommunicationStatus.Success)
            {
                Debug.WriteLine($"Found {result.Services.Count} services:");
                for (int i = 0; i < result.Services.Count; i++)
                {
                    var service = new BluetoothLEAttributeDisplay(result.Services[i]);
                    _services.Add(service);
                    Debug.WriteLine($"#{i:00}: {_services[i].Name}");
                }
            }
            else
            {
                Debug.WriteLine($"Device {deviceName} is unreachable.");
            }
            return result.Status;
        }

        /// <summary>
        /// Disconnect current device and clear list of services and characteristics
        /// </summary>
        public void CloseDevice()
        {
            if (_subscribers.Count > 0) Unsubscribe("all");

            if (_selectedDevice != null)
            {
                Debug.WriteLine($"Device {_selectedDevice.Name} is disconnected.");

                _services?.ForEach((s) => { s.service?.Dispose(); });
                _services?.Clear();
                _characteristics?.Clear();
                _selectedDevice?.Dispose();

                _selectedCharacteristic = null;
                _selectedService = null;
            }
        }

        /// <summary>
        /// Set active service for current device
        /// </summary>
        /// <param name="parameters"></param>
        public async Task<int> OpenService(string serviceName)
        {
            int result = 0;
            if (_selectedDevice != null)
            {
                if (!string.IsNullOrEmpty(serviceName))
                {
                    string foundName = Utilities.GetIdByNameOrNumber(_services, serviceName);

                    // If device is found, connect to device and enumerate all services
                    if (!string.IsNullOrEmpty(foundName))
                    {
                        var attr = _services.FirstOrDefault(s => s.Name.Equals(foundName));
                        IReadOnlyList<GattCharacteristic> characteristics = new List<GattCharacteristic>();

                        try
                        {
                            // Ensure we have access to the device.
                            var accessStatus = await attr.service.RequestAccessAsync();
                            if (accessStatus == DeviceAccessStatus.Allowed)
                            {
                                // BT_Code: Get all the child characteristics of a service. Use the cache mode to specify uncached characterstics only 
                                // and the new Async functions to get the characteristics of unpaired devices as well. 
                                var getCharacteristicResult = await attr.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                                if (getCharacteristicResult.Status == GattCommunicationStatus.Success)
                                {
                                    characteristics = getCharacteristicResult.Characteristics;
                                    _selectedService = attr;
                                    _characteristics.Clear();
                                    Debug.WriteLine($"Selected service {attr.Name}.");

                                    if (characteristics.Count > 0)
                                    {
                                        for (int i = 0; i < characteristics.Count; i++)
                                        {
                                            var charToDisplay = new BluetoothLEAttributeDisplay(characteristics[i]);
                                            _characteristics.Add(charToDisplay);
                                            Debug.WriteLine($"#{i:00}: {charToDisplay.Name}\t{charToDisplay.Chars}");
                                        }
                                    }
                                    else
                                    {
                                        Debug.WriteLine("Service don't have any characteristic.");
                                        result += 1;
                                    }
                                }
                                else
                                {
                                    Debug.WriteLine("Error accessing service.");
                                    result += 1;
                                }
                            }
                            // Not granted access
                            else
                            {
                                Debug.WriteLine("Error accessing service.");
                                result += 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Restricted service. Can't read characteristics: {ex.Message}");
                            result += 1;
                        }
                    }
                    else
                    {
                       
                       Debug.WriteLine("Invalid service name or number");
                        result += 1;
                    }
                }
                else
                {
                    Debug.WriteLine("Invalid service name or number");
                    result += 1;
                }
            }
            else
            {
                Debug.WriteLine("Nothing to use, no BLE device connected.");
                result += 1;
            }

            return result;
        }

        /// <summary>
        /// This function reads data from the specific BLE characteristic 
        /// </summary>
        /// <param name="param"></param>
        public async Task<int> ReadCharacteristic(string param)
        {
            int result = 0;
            if (_selectedDevice != null)
            {
                if (!string.IsNullOrEmpty(param))
                {
                    List<BluetoothLEAttributeDisplay> chars = new List<BluetoothLEAttributeDisplay>();

                    string charName = string.Empty;
                    var parts = param.Split('/');
                    // Do we have parameter is in "service/characteristic" format?
                    if (parts.Length == 2)
                    {
                        string serviceName = Utilities.GetIdByNameOrNumber(_services, parts[0]);
                        charName = parts[1];

                        // If device is found, connect to device and enumerate all services
                        if (!string.IsNullOrEmpty(serviceName))
                        {
                            var attr = _services.FirstOrDefault(s => s.Name.Equals(serviceName));
                            IReadOnlyList<GattCharacteristic> characteristics = new List<GattCharacteristic>();

                            try
                            {
                                // Ensure we have access to the device.
                                var accessStatus = await attr.service.RequestAccessAsync();
                                if (accessStatus == DeviceAccessStatus.Allowed)
                                {
                                    var getCharacteristicResult = await attr.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                                    if (getCharacteristicResult.Status == GattCommunicationStatus.Success)
                                        characteristics = getCharacteristicResult.Characteristics;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Restricted service. Can't read characteristics: {ex.Message}");
                                result += 1;
                            }

                            foreach (var c in characteristics)
                                chars.Add(new BluetoothLEAttributeDisplay(c));
                        }
                    }
                    else if (parts.Length == 1)
                    {
                        if (_selectedService == null)
                        {
                            Debug.WriteLine("No service is selected.");
                        }
                        chars = new List<BluetoothLEAttributeDisplay>(_characteristics);
                        charName = parts[0];
                    }

                    // Read characteristic
                    if (chars.Count > 0 && !string.IsNullOrEmpty(charName))
                    {
                        string useName = Utilities.GetIdByNameOrNumber(chars, charName);
                        var attr = chars.FirstOrDefault(c => c.Name.Equals(useName));
                        if (attr != null && attr.characteristic != null)
                        {
                            // Read characteristic value
                            GattReadResult readResult = await attr.characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);

                            if (readResult.Status == GattCommunicationStatus.Success)
                                Debug.WriteLine(Utilities.FormatValue(readResult.Value, _dataFormat));
                            else
                            {
                                Debug.WriteLine($"Read failed: {readResult.Status}");
                                result += 1;
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Invalid characteristic {charName}");
                            result += 1;
                        }
                    }
                    else
                    {
                        Console.WriteLine("Nothing to read, please specify characteristic name or #.");
                        result += 1;
                    }
                }
                else
                {
                    Console.WriteLine("Nothing to read, please specify characteristic name or #.");
                    result += 1;
                }
            }
            else
            {
                Console.WriteLine("No BLE device connected.");
                result += 1;
            }
            return result;
        }

        /// <summary>
        /// This function writes data from the specific BLE characteristic 
        /// </summary>
        /// <param name="service"></param>
        /// <param name="characterist"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<WriteCharacteristicResult> WriteCharacteristic(string service, string characterist, string data)
        {
            WriteCharacteristicResult result = 0;
            if (_selectedDevice == null)
            {
                Debug.WriteLine("No BLE device connected.");
                return WriteCharacteristicResult.Not_Device;
            }

            List<BluetoothLEAttributeDisplay> chars = new List<BluetoothLEAttributeDisplay>();
            string charName = string.Empty;

            var buffer = Utilities.FormatData(data, _dataFormat);
            if (buffer != null)
            {
                string serviceName = Utilities.GetIdByNameOrNumber(_services, service);
                charName = characterist;

                // If device is found, connect to device and enumerate all services
                if (!string.IsNullOrEmpty(serviceName))
                {
                    var attr = _services.FirstOrDefault(s => s.Name.Equals(serviceName));
                    IReadOnlyList<GattCharacteristic> characteristics = new List<GattCharacteristic>();
                    try
                    {
                        // Ensure we have access to the device.
                        var accessStatus = await attr.service.RequestAccessAsync();
                        if (accessStatus == DeviceAccessStatus.Allowed)
                        {
                            var getCharacteristicResult = await attr.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                            if (getCharacteristicResult.Status == GattCommunicationStatus.Success)
                                characteristics = getCharacteristicResult.Characteristics;
                        }
                        foreach (var c in characteristics)
                            chars.Add(new BluetoothLEAttributeDisplay(c));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Restricted service. Can't read characteristics: {ex.Message}");
                        return WriteCharacteristicResult.Restricted_Service;
                    }
                }
            }

            // Write characteristic
            if (chars.Count > 0 && !string.IsNullOrEmpty(charName))
            {
                string useName = Utilities.GetIdByNameOrNumber(chars, charName);
                var attr = chars.FirstOrDefault(c => c.Name.Equals(useName));
                if (attr != null && attr.characteristic != null)
                {
                    // Write data to characteristic
                    GattWriteResult getCharacteristicResult = await attr.characteristic.WriteValueWithResultAsync(buffer);
                    if (getCharacteristicResult.Status == GattCommunicationStatus.Success)
                    {
                        result = WriteCharacteristicResult.Write_Success;
                    }
                    else
                    {
                        Debug.WriteLine($"Write failed: {getCharacteristicResult.Status}");
                        result = WriteCharacteristicResult.Write_Failed;
                    }
                }
                else
                {
                    Debug.WriteLine($"Invalid characteristic {charName}");
                    result = WriteCharacteristicResult.Invalid_Characteristic;
                }
            }
            else
            {
                Debug.WriteLine("Please specify characteristic name or # for writing.");
                result = WriteCharacteristicResult.Empty_Characteristic;
            }


            return result;
        }

        /// <summary>
        /// This function used to add "ValueChanged" event subscription
        /// </summary>
        /// <param name="param"></param>
        public async Task<int> SubscribeToCharacteristic(string param)
        {
            int result = 0;
            if (_selectedDevice != null)
            {
                if (!string.IsNullOrEmpty(param))
                {
                    List<BluetoothLEAttributeDisplay> chars = new List<BluetoothLEAttributeDisplay>();

                    string charName = string.Empty;
                    var parts = param.Split('/');
                    // Do we have parameter is in "service/characteristic" format?
                    if (parts.Length == 2)
                    {
                        string serviceName = Utilities.GetIdByNameOrNumber(_services, parts[0]);
                        charName = parts[1];

                        // If device is found, connect to device and enumerate all services
                        if (!string.IsNullOrEmpty(serviceName))
                        {
                            var attr = _services.FirstOrDefault(s => s.Name.Equals(serviceName));
                            IReadOnlyList<GattCharacteristic> characteristics = new List<GattCharacteristic>();

                            try
                            {
                                // Ensure we have access to the device.
                                var accessStatus = await attr.service.RequestAccessAsync();
                                if (accessStatus == DeviceAccessStatus.Allowed)
                                {
                                    var getCharacteristicResult = await attr.service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                                    if (getCharacteristicResult.Status == GattCommunicationStatus.Success)
                                        characteristics = getCharacteristicResult.Characteristics;
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Restricted service. Can't subscribe to characteristics: {ex.Message}");
                                result += 1;
                            }

                            foreach (var c in characteristics)
                                chars.Add(new BluetoothLEAttributeDisplay(c));
                        }
                    }
                    else if (parts.Length == 1)
                    {
                        if (_selectedService == null)
                        {
                            Debug.WriteLine("No service is selected.");
                            result += 1;
                            return result;
                        }
                        chars = new List<BluetoothLEAttributeDisplay>(_characteristics);
                        charName = parts[0];
                    }

                    // Read characteristic
                    if (chars.Count > 0 && !string.IsNullOrEmpty(charName))
                    {
                        string useName = Utilities.GetIdByNameOrNumber(chars, charName);
                        var attr = chars.FirstOrDefault(c => c.Name.Equals(useName));
                        if (attr != null && attr.characteristic != null)
                        {
                            // First, check for existing subscription
                            if (!_subscribers.Contains(attr.characteristic))
                            {
                                var status = await attr.characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                                if (status == GattCommunicationStatus.Success)
                                {
                                    _subscribers.Add(attr.characteristic);
                                    attr.characteristic.ValueChanged += Characteristic_ValueChanged;
                                }
                                else
                                {
                                    Debug.WriteLine($"Can't subscribe to characteristic {useName}");
                                    result += 1;
                                }
                            }
                            else
                            {
                                Debug.WriteLine($"Already subscribed to characteristic {useName}");
                                result += 1;
                            }
                        }
                        else
                        {

                            Debug.WriteLine($"Invalid characteristic {useName}");
                            result += 1;
                        }
                    }
                    else
                    {
                        Console.WriteLine("Nothing to subscribe, please specify characteristic name or #.");
                        result += 1;
                    }
                }
                else
                {

                    Debug.WriteLine("Nothing to subscribe, please specify characteristic name or #.");
                    result += 1;
                }
            }
            else
            {

                Debug.WriteLine("No BLE device connected.");
                result += 1;
            }
            return result;
        }

        /// <summary>
        /// This function is used to unsubscribe from "ValueChanged" event
        /// </summary>
        /// <param name="param"></param>
        public async void Unsubscribe(string param)
        {
            if (_subscribers.Count == 0)
            {
                if (!Console.IsOutputRedirected)
                    Console.WriteLine("No subscription for value changes found.");
            }
            else if (string.IsNullOrEmpty(param))
            {
                if (!Console.IsOutputRedirected)
                    Console.WriteLine("Please specify characteristic name or # (for single subscription) or type \"unsubs all\" to remove all subscriptions");
            }
            // Unsubscribe from all value changed events
            else if (param.Replace("/", "").ToLower().Equals("all"))
            {
                foreach (var sub in _subscribers)
                {
                    await sub.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                    sub.ValueChanged -= Characteristic_ValueChanged;
                }
                _subscribers.Clear();
            }
            // unsubscribe from specific event
            else
            {

            }
        }

        /// <summary>
        /// Event handler for ValueChanged callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        public void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            if (_primed)
            {
                var newValue = Utilities.FormatValue(args.CharacteristicValue, _dataFormat);

                Debug.Write($"Value changed for {sender.Uuid}: {newValue}\nBLE: ");
                if (_notifyCompleteEvent != null)
                {
                    _notifyCompleteEvent.Set();
                    _notifyCompleteEvent = null;
                }
            }
            else _primed = true;
        }
    }
}