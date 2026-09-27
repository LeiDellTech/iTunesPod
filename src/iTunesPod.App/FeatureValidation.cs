using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iTunesPod.Core;
using iTunesPod.Infrastructure;
namespace iTunesPod.App;
public static class FeatureValidation
{
    public static async Task RunAsync(string output)
    {
        var folder=Path.GetFullPath(output);Directory.CreateDirectory(folder);var checks=new Dictionary<string,bool>();
        var source=Path.Combine(folder,"source.png");var bitmap=BitmapSource.Create(2,2,96,96,PixelFormats.Bgr24,null,new byte[]{0,0,255,0,255,0,255,0,0,255,255,255},6);NativeImages.SavePng(bitmap,source);
        var photoRoot=Path.Combine(folder,"photo-device");Directory.CreateDirectory(photoRoot);var photo=new NativeImageDatabase(null,true);var payloads=NativeImages.Encode(source,true);var image=photo.AddImage(0,payloads,"Photos/Thumbs","source.png",File.ReadAllBytes(source));
        foreach(var file in image.Files){var path=DeviceAccess.SafePath(photoRoot,file.RelativePath);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,file.Content!);}
        var master=photo.Albums().Single();photo.SetAlbum(master.Id,master.Name,[image.Id]);var album=photo.AddAlbum("旅行",[image.Id]);photo.SetAlbum(album,"新名称",[image.Id]);photo.VerifyFiles(photoRoot,true);checks["NativePhotoPayloadsAndAlbums"]=payloads.Count==4&&photo.Albums().Single(x=>!x.IsMaster).Name=="新名称";
        var reference=photo.Images().Single().Images.Single(x=>x.Format==1024);var preview=NativeImages.Decode(photoRoot,reference,true);checks["PhotoPreviewDecoded"]=preview.PixelWidth==320&&preview.PixelHeight==240;
        var cover=NativeImages.Encode(source,false);checks["CoverFourFormats"]=cover.Select(x=>x.Format).SequenceEqual(new[]{1055,1071,1074,1078});photo.RemoveImage(image.Id);checks["PhotoRemovalUpdatesAllAlbums"]=photo.Images().Count==0&&photo.Albums().All(x=>x.Images.Count==0);
        var flac=Path.Combine(folder,"source.flac");await MediaTools.RunAsync("ffmpeg",["-nostdin","-v","error","-y","-f","lavfi","-i","sine=frequency=440:duration=2","-c:a","flac",flac],CancellationToken.None);
        var tools=new MediaTools(Path.Combine(folder,"cache"));var probe=await tools.ProbeAsync(flac);var track=new Track("audio",flac,"","转换测试","测试","测试","测试",1,1,2026,probe.Seconds,new FileInfo(flac).Length);var audio=await tools.PrepareAsync(track);checks["FlacConvertedToVerifiedAac"]=audio.Path!=flac&&MediaTools.Compatible(await tools.ProbeAsync(audio.Path),false);checks["ConvertedAudioDecoded"]=PlaybackController.DecodeProbe(audio.Path)>0;
        var video=Path.Combine(folder,"source-high.mp4");await MediaTools.RunAsync("ffmpeg",["-nostdin","-v","error","-y","-f","lavfi","-i","testsrc=size=1280x720:rate=60","-f","lavfi","-i","sine=frequency=550:sample_rate=44100","-t","2","-c:v","libx264","-profile:v","high","-pix_fmt","yuv420p","-c:a","aac","-b:a","192k","-ac","2",video],CancellationToken.None);
        var converted=await tools.PrepareAsync(track with{Id="video",Path=video,MediaType=2});var videoProbe=await tools.ProbeAsync(converted.Path);checks["High60FpsVideoConvertedForNano4"]=converted.Path!=video&&MediaTools.Compatible(videoProbe,true)&&videoProbe.Width<=640&&videoProbe.FrameRate<=30.01;
        var adts=Path.Combine(folder,"source.aac");await MediaTools.RunAsync("ffmpeg",["-nostdin","-v","error","-y","-f","lavfi","-i","sine=frequency=330:duration=2","-c:a","aac","-b:a","128k","-ac","2",adts],CancellationToken.None);var remuxed=await tools.PrepareAsync(track with{Id="remux",Path=adts});checks["CompatibleAacRemuxedWithoutReencoding"]=remuxed.Path.Contains("-remux-")&&MediaTools.Compatible(await tools.ProbeAsync(remuxed.Path),false);
        checks["BundledToolsDetected"]=File.Exists(Path.Combine(AppContext.BaseDirectory,"tools/ffmpeg.exe"))&&File.Exists(Path.Combine(AppContext.BaseDirectory,"tools/ffprobe.exe"));
        await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Checks=checks,Audio=await tools.ProbeAsync(audio.Path),Video=videoProbe,SoundOutputTested=false,PhysicalDeviceWritten=false},new JsonSerializerOptions{WriteIndented=true}));
        if(checks.Any(x=>!x.Value))throw new IOException("功能验证未全部通过："+string.Join("、",checks.Where(x=>!x.Value).Select(x=>x.Key)));
    }
}
