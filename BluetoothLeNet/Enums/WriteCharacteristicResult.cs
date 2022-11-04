namespace BluetoothLENet
{
    public partial class BLE
    {
        public enum WriteCharacteristicResult
        {
            Not_Device,
            Empty_Characteristic,
            Invalid_Characteristic,
            Restricted_Service,
            Write_Success,
            Write_Failed,
        }
    }
}
