using iTunesPod.Core;
namespace iTunesPod.Infrastructure;
public static class DeviceWriteProfile
{
    public static bool Supported(DeviceSession device)=>device.ReadError==null&&device.SignatureVerified&&device.SigningIdentity.Length==8&&device.DatabaseVersion==115&&device.Model.Replace(" ","").Contains("Nano第4代",StringComparison.Ordinal);
    public static void Validate(DeviceSession device){if(!Supported(device))throw new IOException("此写入配置仅支持已核验 HASH58、数据库版本 115 的 iPod Nano 4；其他设备保持只读。");}
}
