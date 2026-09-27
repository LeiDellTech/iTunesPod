using System.Text;
using iTunesPod.Core;
namespace iTunesPod.Infrastructure;
public static class PlaylistFiles
{
    public static IReadOnlyList<string> Read(string path){var directory=Path.GetDirectoryName(Path.GetFullPath(path))!;var result=new List<string>();foreach(var line in File.ReadLines(path,Encoding.UTF8)){var value=line.Trim().TrimStart('\ufeff');if(value.Length==0||value.StartsWith('#'))continue;if(Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https")throw new IOException("M3U 网络流不能写入 iPod。");result.Add(Path.GetFullPath(Path.IsPathRooted(value)?value:Path.Combine(directory,value)));if(result.Count>200000)throw new IOException("M3U 曲目超过限制。");}return result;}
    public static void Write(string path,IEnumerable<Track> tracks,bool relative=true){var directory=Path.GetDirectoryName(Path.GetFullPath(path))!;using var writer=new StreamWriter(path,false,new UTF8Encoding(false));writer.WriteLine("#EXTM3U");foreach(var track in tracks){writer.WriteLine("#EXTINF:"+(int)track.Seconds+","+(track.Artist+" - "+track.Title).Replace('\r',' ').Replace('\n',' '));writer.WriteLine(relative?Path.GetRelativePath(directory,track.Path):track.Path);}}
}
