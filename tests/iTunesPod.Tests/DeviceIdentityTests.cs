using iTunesPod.Infrastructure;
namespace iTunesPod.Tests;
public class DeviceIdentityTests
{
    [Fact] public void NanoFourthGenerationComesFromAssociatedUsb(){var identity=DeviceIdentityResolver.Resolve(new Dictionary<string,string>(),new Dictionary<string,string>(),new(UsbPid:"1263",FileSystem:"FAT32"));Assert.Equal("iPod Nano",identity.Family);Assert.Equal("第 4 代",identity.Generation);Assert.Equal("USB 代际已识别",identity.Confidence);}
    [Fact] public void ReadsNanoPszSerialWithoutExposingWholeSerial(){var plain=DeviceIdentityResolver.ReadText("pszSerialNumber: TESTSERIAL123\r\n");Assert.Equal("TESTSERIAL123",DeviceIdentityResolver.SerialFrom(plain,new Dictionary<string,string>(),new()));var result=DeviceIdentityResolver.Resolve(plain,new Dictionary<string,string>(),new());Assert.DoesNotContain("TESTSERIAL",result.SerialMasked);}
    [Fact] public void StaleModelDoesNotOverrideLiveUsb(){var result=DeviceIdentityResolver.Resolve(new Dictionary<string,string>{{"ModelNumStr","MB739"}},new Dictionary<string,string>(),new(UsbPid:"1262"));Assert.NotEqual("第 4 代",result.Generation);Assert.NotEmpty(result.Warnings);Assert.Empty(result.ModelNumber);}
    [Fact] public void NormalizesRegionSpecificModel(){Assert.Equal("MB739",DeviceIdentityResolver.NormalizeModel("xB739CH/A"));}
    [Fact] public void ReadsStandardApplePlistWithoutExternalDtd(){var result=DeviceIdentityResolver.ReadPlist("<!DOCTYPE plist SYSTEM 'file:///does-not-exist'><plist><dict><key>ModelNumStr</key><string>MB739</string></dict></plist>");Assert.Equal("MB739",result["ModelNumStr"]);}
    [Fact] public void FirewireGuidDoesNotReplaceProductSerial(){Assert.Equal("SOURCE123",DeviceIdentityResolver.SerialFrom(new Dictionary<string,string>{{"pszSerialNumber","SOURCE123"}},new Dictionary<string,string>(),new(Serial:"1234567890ABCDEF")));}
    [Fact] public void ManualFolderDoesNotInheritHostUsb(){var result=WindowsDeviceProbe.Inspect(Path.Combine(Path.GetTempPath(),"ipod-image"));Assert.Empty(result.UsbPid);Assert.False(result.IsPhysicalVolume);}
}
