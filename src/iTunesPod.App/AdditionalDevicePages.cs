using System.IO;
using System.Windows.Controls;
using iTunesPod.Infrastructure;
using Microsoft.Win32;
namespace iTunesPod.App;
public sealed partial class MainWindow
{
    async Task RenameDevice()
    {
        if(TargetDevice==null)return;var name=Prompt("设备名称",TargetDevice.Name);if(string.IsNullOrWhiteSpace(name))return;
        var data=await File.ReadAllBytesAsync(DeviceAccess.SafePath(TargetDevice.Root,TargetDevice.DatabaseFile));var lists=new ITunesDbReader().ReadPlaylists(data);
        if(!lists.Any(x=>x.IsSystem))throw new IOException("找不到设备主列表。");
        await CommitFiles([new(TargetDevice.DatabaseFile,DeviceDatabaseEdits.Apply(data,TargetDevice.SigningIdentity,playlists:lists.Select(x=>x.IsSystem?x with{Name=name.Trim()}:x).ToArray()))],"重命名设备");await Discover(true);
    }
    async Task EditPhotoAlbum(NativeImageDatabase database,NativePhotoAlbum album)
    {
        if(album.IsMaster){Notify("系统相册包含全部照片。");return;}
        var entries=new ListBox{SelectionMode=SelectionMode.Multiple,MinHeight=240,ItemsSource=database.Images().Select(x=>x.Id).ToArray()};foreach(var id in album.Images)entries.SelectedItems.Add(id);
        if(!ShowDialog("相册照片 · "+album.Name,Stack(T("选择需要保留在此相册的照片编号",13),entries),true))return;
        database.SetAlbum(album.Id,album.Name,entries.SelectedItems.Cast<int>().ToArray());await CommitFiles([database.Change("Photos/Photo Database")],"编辑相册照片");DevicePhotos();
    }
    async Task ExportOriginalPhoto(NativeImage image)
    {
        var reference=image.Images.FirstOrDefault(x=>x.Format==1&&x.Width==0);if(reference==null){Notify("设备没有保留此照片原图，可导出预览图。");return;}
        var path=DeviceAccess.SafePath(TargetDevice!.Root,"Photos/"+reference.FileName.TrimStart(':').Replace(':','/'));
        var picker=new SaveFileDialog{FileName="照片-"+image.Id+Path.GetExtension(path),Filter="原图|*.*"};if(picker.ShowDialog(this)!=true)return;
        if(Path.GetFullPath(picker.FileName).StartsWith(Path.GetFullPath(TargetDevice.Root),StringComparison.OrdinalIgnoreCase))throw new IOException("请选择电脑导出目录。");
        await using var source=File.OpenRead(path);if(reference.Offset+reference.Size>source.Length)throw new IOException("原图引用越界。");source.Position=reference.Offset;await using var output=new FileStream(picker.FileName,FileMode.Create,FileAccess.Write);var buffer=new byte[81920];var remaining=reference.Size;while(remaining>0){var n=await source.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,remaining)),_lifetime.Token);if(n==0)throw new IOException("原图被截断。");await output.WriteAsync(buffer.AsMemory(0,n),_lifetime.Token);remaining-=n;}
    }
}
