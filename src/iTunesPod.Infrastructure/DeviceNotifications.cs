using System.IO;
using System.Management;
using System.Runtime.Versioning;

namespace iTunesPod.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class DeviceNotifications : IDisposable
{
    private ManagementEventWatcher? _watcher;
    public event Action? Changed;
    public void Start()
    {
        try
        {
            _watcher=new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent"));
            _watcher.EventArrived+=(_,e)=>{if(Convert.ToUInt16(e.NewEvent["EventType"])==3&&e.NewEvent["DriveName"] is string root)DeviceAccess.InvalidateMount(root.EndsWith(Path.DirectorySeparatorChar)?root:root+Path.DirectorySeparatorChar);Changed?.Invoke();}; _watcher.Start();
        }
        catch(ManagementException) { _watcher?.Dispose(); _watcher=null; }
    }
    public void Dispose() { try { _watcher?.Stop(); } catch(ManagementException) { } _watcher?.Dispose(); }
}
