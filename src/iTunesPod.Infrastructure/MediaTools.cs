using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using iTunesPod.Core;
namespace iTunesPod.Infrastructure;

public sealed record MediaProbe(string AudioCodec,string VideoCodec,int Width,int Height,double FrameRate,string Profile,int Level,int Channels,int SampleRate,long BitRate,double Seconds,string AudioProfile="LC",long AudioBitRate=0);
public sealed class MediaTools(string cache,string ffmpeg="ffmpeg",string ffprobe="ffprobe")
{
    public static async Task<string> RunAsync(string command,IEnumerable<string> args,CancellationToken token)
    {
        if(command is "ffmpeg" or "ffprobe"){var bundled=Path.Combine(AppContext.BaseDirectory,"tools",command+".exe");if(File.Exists(bundled))command=bundled;}
        var info=new ProcessStartInfo(command){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new IOException("工具启动失败："+command);var stdout=process.StandardOutput.ReadToEndAsync(token);var stderr=process.StandardError.ReadToEndAsync(token);
        try{await process.WaitForExitAsync(token);}catch{try{process.Kill(true);}catch(InvalidOperationException){}throw;}
        var output=await stdout;var error=await stderr;if(process.ExitCode!=0)throw new IOException(command+"："+error[^Math.Min(error.Length,2000)..]);return output;
    }
    public async Task<MediaProbe> ProbeAsync(string path,CancellationToken token=default)
    {
        using var json=JsonDocument.Parse(await RunAsync(ffprobe,["-v","error","-show_streams","-show_format","-of","json",path],token));
        var streams=json.RootElement.GetProperty("streams").EnumerateArray().ToArray();var audio=streams.FirstOrDefault(x=>Text(x,"codec_type")=="audio");var video=streams.FirstOrDefault(x=>Text(x,"codec_type")=="video"&&!(x.TryGetProperty("disposition",out var disposition)&&Number(disposition,"attached_pic")==1));var format=json.RootElement.GetProperty("format");var rate=Text(video,"avg_frame_rate").Split('/');var fps=rate.Length==2&&double.TryParse(rate[0],out var top)&&double.TryParse(rate[1],out var bottom)&&bottom!=0?top/bottom:0;
        return new(Text(audio,"codec_name"),Text(video,"codec_name"),Number(video,"width"),Number(video,"height"),fps,Text(video,"profile"),Number(video,"level"),Number(audio,"channels"),Number(audio,"sample_rate"),long.TryParse(Text(format,"bit_rate"),out var bits)?bits:0,double.TryParse(Text(format,"duration"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var seconds)?seconds:0,Text(audio,"profile"),long.TryParse(Text(audio,"bit_rate"),out var audioBits)?audioBits:0);
    }
    static string Text(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var value)?value.ToString():"";
    static int Number(JsonElement e,string key)=>int.TryParse(Text(e,key),out var n)?n:0;
    public static bool Compatible(MediaProbe p,bool video)=>p.Seconds>0&&p.Channels is >0 and <=2&&p.SampleRate is >0 and <=48000&&(video?p.VideoCodec=="h264"&&p.Profile.Contains("Baseline",StringComparison.OrdinalIgnoreCase)&&p.Level<=30&&p.Width<=640&&p.Height<=480&&p.FrameRate>0&&p.FrameRate<=30.01&&p.AudioCodec=="aac"&&p.AudioProfile=="LC"&&(p.AudioBitRate==0||p.AudioBitRate<=160000)&&p.Channels<=2&&p.SampleRate<=48000&&p.BitRate<=2_660_000:p.VideoCodec.Length==0&&p.AudioCodec is "mp3" or "aac" or "alac" or "pcm_s16le"&&(p.AudioCodec!="aac"||p.AudioProfile=="LC")&&(p.AudioCodec is not ("mp3" or "aac")||p.AudioBitRate==0||p.AudioBitRate<=320000));
    public async Task<Track> PrepareAsync(Track track,CancellationToken token=default)
    {
        await using var source=File.OpenRead(track.Path);var hash=Convert.ToHexString(await SHA256.HashDataAsync(source,token));if(track.Hash.Length==64&&!hash.Equals(track.Hash,StringComparison.OrdinalIgnoreCase))throw new IOException("来源内容已更改，请重新导入："+track.Title);
        var probe=await ProbeAsync(track.Path,token);var ext=Path.GetExtension(track.Path).ToLowerInvariant();if(Compatible(probe,track.IsVideo)&&ext is ".mp3" or ".m4a" or ".mp4" or ".m4v" or ".wav")return track with{Seconds=probe.Seconds,SampleRate=(uint)probe.SampleRate,Bytes=source.Length,Bitrate=(uint)(probe.AudioBitRate/1000)};
        if(source.Length>uint.MaxValue)throw new IOException("媒体超过 Nano 4 / FAT32 单文件上限。");Directory.CreateDirectory(cache);var toolVersion=(await RunAsync(ffmpeg,["-version"],token)).Split((char)10)[0];var toolKey=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(toolVersion)))[..12];var remux=Compatible(probe,track.IsVideo)&&probe.AudioCodec is "aac" or "alac";var target=Path.Combine(cache,hash+"-"+toolKey+(remux?"-remux":"")+(track.IsVideo?"-nano4-video-v2.mp4":"-nano4-audio-v2.m4a"));if(!File.Exists(target)){
            var temp=target+".partial";var args=new List<string>{"-nostdin","-v","error","-y","-i",track.Path,"-map","0:a:0","-c:a","aac","-b:a",track.IsVideo?"128k":"160k","-ar","44100","-ac","2"};
            if(track.IsVideo)args.AddRange(["-map","0:v:0","-c:v","libx264","-profile:v","baseline","-level:v","3.0","-pix_fmt","yuv420p","-vf","scale=640:480:force_original_aspect_ratio=decrease:force_divisible_by=2","-r","30","-b:v","1400k","-maxrate","1500k","-bufsize","3000k"]);else args.Add("-vn");if(remux){args=["-nostdin","-v","error","-y","-i",track.Path,"-map","0:a:0","-c:a","copy"];if(track.IsVideo)args.AddRange(["-map","0:v:0","-c:v","copy"]);}args.AddRange(["-movflags","+faststart","-f","mp4",temp]);
            try{await RunAsync(ffmpeg,args,token);var verified=await ProbeAsync(temp,token);if(!Compatible(verified,track.IsVideo)||verified.Seconds<=0)throw new IOException("转换输出未通过 Nano 4 格式校验。");File.Move(temp,target);}catch{if(File.Exists(temp))File.Delete(temp);throw;}
        }
        var output=await ProbeAsync(target,token);if(!Compatible(output,track.IsVideo))throw new IOException("缓存媒体不兼容，请清理后重试。");return track with{Path=target,Bytes=new FileInfo(target).Length,Seconds=output.Seconds,SampleRate=(uint)output.SampleRate,Bitrate=(uint)(output.AudioBitRate/1000)};
    }
}
