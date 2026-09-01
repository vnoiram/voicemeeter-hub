namespace VoicemeeterHub;

public enum VoicemeeterDeviceDriver
{
    Mme = 1,
    Ks = 4,
    Wdm = 3,
    Asio = 5
}

public sealed record VoicemeeterDeviceInfo(int Index, VoicemeeterDeviceDriver Driver, string Name, string HardwareId)
{
    public string DriverParamValue => Driver switch
    {
        VoicemeeterDeviceDriver.Mme => "mme",
        VoicemeeterDeviceDriver.Ks => "ks",
        VoicemeeterDeviceDriver.Wdm => "wdm",
        VoicemeeterDeviceDriver.Asio => "asio",
        _ => "wdm"
    };

    public string CompositeId => $"{DriverParamValue}:{Name}";
}
