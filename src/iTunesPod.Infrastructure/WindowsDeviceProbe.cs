using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace iTunesPod.Infrastructure;

public static class WindowsDeviceProbe
{
    public static string VolumeIdentity(string root)
    {
        if(!OperatingSystem.IsWindows()) return "";
        var path=Path.GetPathRoot(Path.GetFullPath(root))!;
        var volume=new StringBuilder(1024);
        return GetVolumeNameForVolumeMountPoint(path,volume,volume.Capacity) ? volume.ToString() : "";
    }
    public static HardwareFacts Inspect(string root)
    {
        if(!OperatingSystem.IsWindows()) return new();
        var full=Path.GetFullPath(root).TrimEnd('\\','/');
        var volumeRoot=Path.GetPathRoot(Path.GetFullPath(root))!;
        // Folder images never inherit the host disk's USB identity.
        if(!string.Equals(full,volumeRoot.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)) return new();
        return InspectWindows(volumeRoot);
    }
    [SupportedOSPlatform("windows")]
    private static HardwareFacts InspectWindows(string root)
    {
        var volume=VolumeIdentity(root); var fs=new DriveInfo(root).DriveFormat;
        var pnp=""; var pid=""; var serial=""; var revision=""; var guid="";
        try
        {
            using var disk = new ManagementObject($"Win32_LogicalDisk.DeviceID='{root[..2]}'");
            using var partitions = disk.GetRelated("Win32_DiskPartition");
            foreach(ManagementObject partition in partitions)
            using(partition)
            {
                using var drives=partition.GetRelated("Win32_DiskDrive");
                foreach(ManagementObject drive in drives)
                using(drive)
                {
                    pnp=drive["PNPDeviceID"]?.ToString() ?? "";
                    var instanceGuid=Regex.Match(pnp,@"(?:\\\\|&)([0-9A-F]{16})(?:&|$)",RegexOptions.IgnoreCase);if(instanceGuid.Success)guid=instanceGuid.Groups[1].Value;
                    serial=drive["SerialNumber"]?.ToString()?.Trim() ?? "";
                    if(CM_Locate_DevNode(out var node,pnp,0)!=0) continue;
                    for(var depth=0;depth<12;depth++)
                    {
                        var id=new StringBuilder(1024);
                        if(CM_Get_Device_ID(node,id,id.Capacity,0)==0)
                        {
                            var match=Regex.Match(id.ToString(),@"VID_05AC&PID_([0-9A-F]{4})",RegexOptions.IgnoreCase);
                            if(match.Success) { pid=match.Groups[1].Value;var serialMatch=Regex.Match(id.ToString(),@"\\\\([0-9A-F]{16})(?:&.*)?$",RegexOptions.IgnoreCase);if(serialMatch.Success){guid=serialMatch.Groups[1].Value;break;} }
                        }
                        if(CM_Get_Parent(out var parent,node,0)!=0) break;
                        node=parent;
                    }
                }
            }
        }
        catch(Exception e) when(e is ManagementException or UnauthorizedAccessException or COMException) { /* File identity is still available. */ }
        // Metadata query only. No raw-disk writes, volume locks or SCSI data-out commands.
        using(var handle=CreateFile(@"\\.\"+root[..2],0,3,IntPtr.Zero,3,0,IntPtr.Zero))
        {
            var data=new byte[4096]; var query=new byte[12];
            if(!handle.IsInvalid && DeviceIoControl(handle,0x002D1400,query,query.Length,data,data.Length,out var returned,IntPtr.Zero) && returned>=36)
            {
                string Field(int offset)
                {
                    var start=BitConverter.ToUInt32(data,offset); if(start==0||start>=returned) return "";
                    var end=(int)start; while(end<returned && data[end]!=0) end++;
                    return Encoding.ASCII.GetString(data,(int)start,end-(int)start).Trim();
                }
                var deviceSerial=Field(24); if(deviceSerial.Length>0) serial=deviceSerial;
                revision=Field(20);
            }
        }
        var hardwareKey=pnp.Length==0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pnp.ToUpperInvariant())));
        return new(volume,fs,pid,hardwareKey,serial,revision,true){FirewireGuid=guid};
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="GetVolumeNameForVolumeMountPointW")]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeNameForVolumeMountPoint(string path,StringBuilder name,int length);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode,EntryPoint="CM_Locate_DevNodeW")] private static extern int CM_Locate_DevNode(out uint node,string id,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode,EntryPoint="CM_Get_Device_IDW")] private static extern int CM_Get_Device_ID(uint node,StringBuilder id,int length,uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent,uint node,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")] private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint attributes,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,int inputLength,byte[] output,int outputLength,out int returned,IntPtr overlapped);
}


