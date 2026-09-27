using System.Text.RegularExpressions;
namespace iTunesPod.Infrastructure;
public static class LogRedactor
{
    public static string Redact(string value)
    {
        value=Regex.Replace(value,@"https?://[^\s<>""']+",m=>Uri.TryCreate(m.Value,UriKind.Absolute,out var uri)?uri.GetLeftPart(UriPartial.Path)+(uri.Query.Length>0?"?[参数已隐藏]":""):"[网络地址]",RegexOptions.IgnoreCase);
        value=Regex.Replace(value,@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\r\n]*","[本地路径]");
        value=Regex.Replace(value,@"\\\\[^\r\n]+","[网络路径]");
        value=Regex.Replace(value,@"ipod:[0-9a-f]{64}","ipod:[设备标识已隐藏]",RegexOptions.IgnoreCase);
        if(Environment.UserName.Length>0)value=value.Replace(Environment.UserName,"[用户名]",StringComparison.OrdinalIgnoreCase);
        return value;
    }
}
