using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using iTunesPod.Core;
namespace iTunesPod.Infrastructure;
public static class DeviceEject
{
    [SupportedOSPlatform("windows")]
    public static async Task EjectAsync(DeviceSession session,CancellationToken token=default)
    {
        var root=Path.GetPathRoot(session.Root)!;if(!Path.GetFullPath(session.Root).TrimEnd('\\','/').Equals(root.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))return;
        var current=await new DeviceAccess().InspectAsync(root,token);if(current.DeviceId!=session.DeviceId||current.Identity.VolumeIdentity!=session.Identity.VolumeIdentity)throw new IOException("挂载设备已更改。");if(File.Exists(DeviceAccess.SafePath(root,"iPod_Control/iTunes/.itp-lock")))throw new IOException("设备有未结束的写入任务，不能弹出。");
        using var logical=new ManagementObject($"Win32_LogicalDisk.DeviceID='{root[..2]}'");using var partitions=logical.GetRelated("Win32_DiskPartition");string? pnp=null;foreach(ManagementObject partition in partitions)using(partition){using var drives=partition.GetRelated("Win32_DiskDrive");foreach(ManagementObject drive in drives)using(drive){if(pnp!=null)throw new IOException("卷对应多个物理设备，不能自动弹出。");pnp=drive["PNPDeviceID"]?.ToString();}}
        if(pnp==null||CM_Locate_DevNode(out var node,pnp,0)!=0)throw new IOException("找不到当前卷对应的设备节点。");var veto=new StringBuilder(512);var code=CM_Request_Device_Eject(node,out var type,veto,veto.Capacity,0);if(code!=0)throw new IOException("Windows 拒绝弹出设备（占用类型 "+type+"）。请关闭正在访问 iPod 的程序后重试。");for(var i=0;i<50;i++){token.ThrowIfCancellationRequested();if(!Directory.Exists(Path.Combine(root,"iPod_Control")))return;await Task.Delay(200,token);}throw new IOException("Windows 已收到弹出请求，但设备仍挂载，尚不能确认安全移除。");
    }
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode,EntryPoint="CM_Locate_DevNodeW")]static extern uint CM_Locate_DevNode(out uint node,string id,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode,EntryPoint="CM_Request_Device_EjectW")]static extern uint CM_Request_Device_Eject(uint node,out int vetoType,StringBuilder vetoName,int length,uint flags);
}
