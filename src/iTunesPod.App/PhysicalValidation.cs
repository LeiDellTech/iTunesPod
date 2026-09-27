using System.IO;
using System.Text.Json;
using iTunesPod.Core;
using iTunesPod.Infrastructure;
namespace iTunesPod.App;

public static class PhysicalValidation
{
    public static async Task RunAsync(string root,string output)
    {
        var folder=Path.GetFullPath(output);Directory.CreateDirectory(folder);var host=Path.Combine(folder,"host-data");
        var device=await new DeviceAccess().InspectAsync(root);DeviceWriteProfile.Validate(device);
        var before=await DeviceBackup.MetadataHashesAsync(device.Root);var beforeMedia=device.Tracks.Select(x=>x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var source=Path.Combine(folder,"iTunesPod-physical-test.mp3");
        await MediaTools.RunAsync("ffmpeg",["-nostdin","-v","error","-y","-f","lavfi","-i","sine=frequency=523.25:duration=2","-c:a","libmp3lame","-b:a","128k",source],CancellationToken.None);
        var track=new Track("physical-roundtrip",source,"","iTunesPod 写入测试","iTunesPod","设备验证","iTunesPod",1,1,2026,2,new FileInfo(source).Length);
        var sync=new DeviceSync(host);var plan=await sync.PrepareAsync(device,[track]);
        if(plan.Media.Count!=1||plan.ExistingMatches.Count!=0)throw new IOException("测试内容没有生成唯一新增项目。");
        var media=plan.Media.Single();var target=DeviceAccess.SafePath(device.Root,media.Write.RelativePath);var journal=Path.Combine(plan.WorkDirectory,"journal.json");
        var recovered=false;string? backup=null;DeviceSession? written=null;
        try
        {
            backup=await sync.CommitAsync(plan);written=await new DeviceAccess().InspectAsync(device.Root);
            if(!written.SignatureVerified||written.Generation!=plan.DatabaseHash)throw new IOException("真实设备提交后的数据库签名或哈希不匹配。");
            if(written.Tracks.Count!=device.Tracks.Count+1||!written.Tracks.Any(x=>x.Id=="device:"+media.Write.Id&&File.Exists(x.Path)))throw new IOException("真实设备未找到新增曲目或媒体。");
            if(await DeviceBackup.HashAsync(target)!=media.PayloadHash)throw new IOException("真实设备媒体复读哈希不匹配。");
            await sync.RecoverAsync(written,journal);recovered=true;
        }
        finally
        {
            if(!recovered&&File.Exists(journal))
            {
                var state=JsonSerializer.Deserialize<SyncJournal>(await File.ReadAllTextAsync(journal));
                if(state?.Phase is "Complete" or "Committing" or "RecoveryRequired")await sync.RecoverAsync(await new DeviceAccess().InspectAsync(device.Root),journal);
            }
        }
        var restored=await new DeviceAccess().InspectAsync(device.Root);var after=await DeviceBackup.MetadataHashesAsync(device.Root);var afterMedia=restored.Tracks.Select(x=>x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checks=new Dictionary<string,bool>{
            ["WriteProfileAccepted"]=DeviceWriteProfile.Supported(device),
            ["CommittedHash58Verified"]=written?.SignatureVerified==true,
            ["TrackAddedAndMediaVerified"]=written?.Tracks.Count==device.Tracks.Count+1,
            ["RecoveryCompleted"]=recovered,
            ["DatabaseRestoredExact"]=restored.Generation==device.Generation,
            ["MetadataRestoredExact"]=before.Count==after.Count&&before.All(x=>after.GetValueOrDefault(x.Key)==x.Value),
            ["MediaSetRestoredExact"]=beforeMedia.SetEquals(afterMedia)&&!File.Exists(target)
        };
        await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Device=device.Model,device.DatabaseVersion,BeforeTracks=device.Tracks.Count,WrittenTracks=written?.Tracks.Count,RestoredTracks=restored.Tracks.Count,TestMedia=media.Write.RelativePath,Backup=backup,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
        if(checks.Any(x=>!x.Value))throw new IOException("真实设备往返验证未完全通过："+string.Join("、",checks.Where(x=>!x.Value).Select(x=>x.Key)));
    }
}
