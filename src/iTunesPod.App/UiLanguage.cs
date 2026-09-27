using System.Runtime.InteropServices;
using System.Text;
namespace iTunesPod.App;
public static class UiLanguage
{
    public static bool Traditional {get;set;}
    public static string Label(string value)=>Traditional&&!System.IO.Path.IsPathRooted(value)?Convert(value):value;
    static string Convert(string value)
    {
        if(string.IsNullOrEmpty(value))return value;
        var buffer=new StringBuilder(value.Length+1);
        try{var length=LCMapStringEx("zh-Hant",0x04000000,value,value.Length,buffer,buffer.Capacity,IntPtr.Zero,IntPtr.Zero,0);return length>0?buffer.ToString(0,length):value;}
        catch(DllNotFoundException){return value;}
        catch(EntryPointNotFoundException){return value;}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern int LCMapStringEx(string localeName,uint mapFlags,string source,int sourceLength,StringBuilder destination,int destinationLength,IntPtr version,IntPtr reserved,nint sortHandle);
}
