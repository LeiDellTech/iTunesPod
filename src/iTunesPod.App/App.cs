using System.IO;
using System.Text.Json;
using System.Windows;
namespace iTunesPod.App;
public class App : Application
{
    [STAThread] public static void Main(string[] args){
        if(args.Length==2&&args[0]=="--feature-check"){
            try{FeatureValidation.RunAsync(args[1]).GetAwaiter().GetResult();}catch(Exception e){Directory.CreateDirectory(args[1]);File.WriteAllText(Path.Combine(args[1],"error.txt"),e.ToString());Environment.ExitCode=1;}return;
        }
        if(args.Length==3&&args[0]=="--sync-mirror"){
            try{MirrorValidation.RunAsync(args[1],args[2]).GetAwaiter().GetResult();}
            catch(Exception e){Directory.CreateDirectory(args[2]);File.WriteAllText(Path.Combine(args[2],"error.txt"),e.ToString());Environment.ExitCode=1;}return;
        }
        if(args.Length==3&&args[0]=="--writer-check"){
            try{var device=new iTunesPod.Infrastructure.DeviceAccess().InspectAsync(args[1]).GetAwaiter().GetResult();var original=File.ReadAllBytes(Path.Combine(device.Root,device.DatabaseFile));var writer=new iTunesPod.Infrastructure.ITunesDbWriter();var unchanged=writer.Build(original,[],device.SigningIdentity);var id=device.Tracks.Select(x=>uint.Parse(x.Id[7..])).Max()+1;var example=device.Tracks.First(x=>File.Exists(x.Path)) with{Title="电脑副本写入验证"};var result=writer.Build(original,[new(id,example,"iPod_Control/Music/F00/fixture.mp3")],device.SigningIdentity);Directory.CreateDirectory(args[2]);File.WriteAllBytes(Path.Combine(args[2],"iTunesDB"),result);var reader=new iTunesPod.Infrastructure.ITunesDbReader();File.WriteAllText(Path.Combine(args[2],"report.json"),JsonSerializer.Serialize(new{NoOpExact=original.SequenceEqual(unchanged),Signature=iTunesPod.Infrastructure.DeviceChecksum.VerifyHash58(result,device.SigningIdentity),OriginalTracks=device.Tracks.Count,OutputTracks=reader.Read(result,args[2]).Count,Playlists=reader.ReadPlaylists(result).Select(x=>new{x.Name,Count=x.TrackIds.Count,x.IsSystem}),LiveDeviceWritten=false},new JsonSerializerOptions{WriteIndented=true}));}
            catch(Exception e){Directory.CreateDirectory(args[2]);File.WriteAllText(Path.Combine(args[2],"error.txt"),e.ToString());Environment.ExitCode=1;}return;
        }
        if(args.Length==3&&args[0]=="--physical-roundtrip"){
            try{PhysicalValidation.RunAsync(args[1],args[2]).GetAwaiter().GetResult();}
            catch(Exception e){Directory.CreateDirectory(args[2]);File.WriteAllText(Path.Combine(args[2],"error.txt"),e.ToString());Environment.ExitCode=1;}return;
        }
        if(args.Length==3&&args[0]=="--inspect-device"){
            try{var session=new iTunesPod.Infrastructure.DeviceAccess().InspectAsync(args[1]).GetAwaiter().GetResult();File.WriteAllText(args[2],JsonSerializer.Serialize(session,new JsonSerializerOptions{WriteIndented=true}));}
            catch(Exception e){File.WriteAllText(args[2],e.ToString());Environment.ExitCode=1;}return;
        }
        var app=new App{ShutdownMode=ShutdownMode.OnMainWindowClose};app.DispatcherUnhandledException+=(_,e)=>{iTunesPod.App.MainWindow.ShowUnhandled(e.Exception.Message);e.Handled=true;};app.Run(new iTunesPod.App.MainWindow(args));
    }
}
