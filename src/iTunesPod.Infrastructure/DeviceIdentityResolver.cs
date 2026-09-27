using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using iTunesPod.Core;

namespace iTunesPod.Infrastructure;

public sealed record HardwareFacts(string VolumeId = "", string FileSystem = "", string UsbPid = "",
    string HardwareKey = "", string Serial = "", string Firmware = "", bool IsPhysicalVolume = false)
{ public string FirewireGuid { get; init; } = ""; }

public static class DeviceIdentityResolver
{
    private sealed record Model(string Family, string Generation, string Capacity = "", string Color = "");
    private sealed record Catalog(Dictionary<string, Model> Models, Dictionary<string, string> SerialSuffixes, Dictionary<string, Model> Usb);
    private static readonly Catalog Data = LoadCatalog();
    private static Catalog LoadCatalog()
    {
        using var stream = typeof(DeviceIdentityResolver).Assembly.GetManifestResourceStream("iTunesPod.Infrastructure.Data.ipod-models.json")!;
        return JsonSerializer.Deserialize<Catalog>(stream)!;
    }
    public static Dictionary<string,string> ReadText(string text) => text.Split('\n').Where(x=>x.Contains(':'))
        .Select(x=>x.Split(':',2)).GroupBy(x=>x[0].Trim(),StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x=>x.Key,x=>x.Last()[1].Trim(),StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string,string> ReadPlist(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text),new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        var xml = XDocument.Load(reader);
        var dictionary = xml.Root?.Name.LocalName == "plist" ? xml.Root.Element("dict") : xml.Root;
        var elements = dictionary?.Elements().ToArray() ?? [];
        var output = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for(var i=0;i+1<elements.Length;i++)
            if(elements[i].Name.LocalName == "key" && elements[i+1].Name.LocalName is "string" or "integer") output[elements[i].Value]=elements[i+1].Value;
        return output;
    }
    public static string NormalizeModel(string value)
    {
        var text = value.Trim().ToUpperInvariant();
        if(text.Length>1 && text[0]!='M') text="M"+text[1..];
        return Data.Models.Keys.Where(k=>text.StartsWith(k,StringComparison.Ordinal)).OrderByDescending(k=>k.Length).FirstOrDefault() ?? text;
    }
    public static string SerialFrom(IReadOnlyDictionary<string,string> plain,IReadOnlyDictionary<string,string> extended,HardwareFacts hardware)
    {
        // A 16-character hexadecimal USB serial is a FireWire GUID, not an Apple product serial.
        var live=hardware.Serial.Trim();
        if(live.Length>0 && !Regex.IsMatch(live,"^[0-9a-fA-F]{16}$")) return live;
        return Get(extended,"SerialNumber","pszSerialNumber") is { Length: >0 } serial ? serial : Get(plain,"pszSerialNumber","SerialNumber");
    }
    private static string Get(IReadOnlyDictionary<string,string> dict,params string[] names) => names.Select(n=>dict.GetValueOrDefault(n,"")).FirstOrDefault(v=>v.Length>0) ?? "";
    public static DeviceIdentity Resolve(IReadOnlyDictionary<string,string> plain,IReadOnlyDictionary<string,string> extended,HardwareFacts hardware)
    {
        var evidence = new List<IdentityEvidence>(); var warnings = new List<string>();
        var serial=SerialFrom(plain,extended,hardware);
        var rawModel=Get(extended,"ModelNumStr"); var modelSource="SysInfoExtended 型号";
        if(rawModel.Length==0) { rawModel=Get(plain,"ModelNumStr"); modelSource="SysInfo 型号"; }
        var modelNumber=rawModel.Length>0 ? NormalizeModel(rawModel) : "";
        if(modelNumber.Length==0 && serial.Length>=3)
        {
            foreach(var suffixLength in new[]{4,3})
                if(serial.Length>=suffixLength && Data.SerialSuffixes.TryGetValue(serial[^suffixLength..].ToUpperInvariant(),out var number))
                { modelNumber=number; modelSource="Apple 序列号后缀型号表"; break; }
        }
        Data.Models.TryGetValue(modelNumber,out var model);
        Data.Usb.TryGetValue(hardware.UsbPid.ToUpperInvariant(),out var usb);
        if(hardware.UsbPid.Length>0) evidence.Add(new("USB 标识","05AC:"+hardware.UsbPid.ToUpperInvariant(),"Windows 当前卷 → 磁盘 → USB 父设备"));
        if(model!=null) evidence.Add(new("型号",modelNumber,modelSource));
        // Live, specific USB family/generation must not be overridden by a stale SysInfo cache.
        if(model!=null && usb!=null && usb.Generation.Length>0 &&
            (!string.Equals(model.Family,usb.Family,StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(model.Generation,usb.Generation,StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("缓存型号与当前 USB 设备不一致，使用实时 USB 代际；具体型号和颜色需进一步确认。");
            model=null; modelNumber="";
        }
        var family=model?.Family ?? usb?.Family ?? "iPod";
        var generation=model?.Generation ?? usb?.Generation ?? "";
        generation = Regex.Replace(generation,@"^(\d+(?:\.\d+)?)(?:st|nd|rd|th) Gen", "第 $1 代",RegexOptions.IgnoreCase);
        var confidence=model!=null ? "型号已匹配" : usb!=null && generation.Length>0 ? "USB 代际已识别" : "型号待确认";
        var firmware=hardware.Firmware.Length>0 ? hardware.Firmware : Get(extended,"VisibleBuildID","visibleBuildID","BuildID","FirmwareVersion");
        if(firmware.Length==0) firmware=Get(plain,"visibleBuildID","VisibleBuildID","buildID","FirmwareVersion");
        if(firmware.Length>0) evidence.Add(new("固件修订",firmware,hardware.Firmware.Length>0 ? "Windows 存储设备描述符" : "设备配置文件"));
        if(hardware.FileSystem.Length>0) evidence.Add(new("文件系统",hardware.FileSystem,"Windows 卷信息"));
        if(serial.Length>0) evidence.Add(new("设备序列号","••••"+serial[^Math.Min(4,serial.Length)..],"设备身份信息（已遮蔽）"));
        if(hardware.UsbPid.StartsWith("122") || hardware.UsbPid.StartsWith("123") || hardware.UsbPid.StartsWith("124")) warnings.Add("USB 标识属于恢复模式，不能作为正常同步设备。");
        return new(family,generation,modelNumber,model?.Color ?? "",firmware,serial.Length>0 ? "••••"+serial[^Math.Min(4,serial.Length)..] : "未提供",
            hardware.UsbPid.Length>0 ? "05AC:"+hardware.UsbPid.ToUpperInvariant() : "",hardware.VolumeId,hardware.FileSystem,confidence,evidence,warnings);
    }
}

