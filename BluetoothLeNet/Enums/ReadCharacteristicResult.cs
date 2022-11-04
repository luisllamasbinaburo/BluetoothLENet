namespace BluetoothLENet
{
    public partial class BLE
    {
        public enum ReadCharacteristicResult
        {
            Not_Device,
            Empty_Characteristic,
            Invalid_Characteristic,
            Restricted_Service,
            Read_Success,
            Read_Failed,
        }
    }
}
