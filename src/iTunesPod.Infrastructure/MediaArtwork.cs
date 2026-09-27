using iTunesPod.Core;
using System.Security.Cryptography;
using System.Text;

namespace iTunesPod.Infrastructure;

public static class MediaArtwork
{
    // All source handles are explicitly read-only, including files stored on the iPod.
    private sealed class ReadOnlyMedia(string path) : TagLib.File.IFileAbstraction
    {
        public string Name => path;
        public Stream ReadStream => new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        public Stream WriteStream => throw new NotSupportedException("设备媒体只读。");
        public void CloseStream(Stream stream) => stream.Dispose();
    }
    public static async Task<IReadOnlyList<Track>> ReadCoversAsync(IReadOnlyList<Track> tracks,string cache,CancellationToken token,IProgress<int>? progress=null)
    {
        Directory.CreateDirectory(cache); var result=tracks.ToArray(); var completed=0;
        foreach(var group in tracks.Select((track,index)=>(track,index)).GroupBy(x=>x.track.AlbumKey))
        {
            token.ThrowIfCancellationRequested(); string? cover=null;
            var sample=group.First().track;
            var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sample.Path+sample.Bytes+sample.AlbumKey)));
            var cacheFile=Path.Combine(cache,key+".img");
            if(File.Exists(cacheFile)) cover=cacheFile;
            else
            {
                foreach(var (track,_) in group.Take(2))
                {
                    try
                    {
                        if(!File.Exists(track.Path)) continue;
                        using var file=TagLib.File.Create(new ReadOnlyMedia(track.Path));
                        var picture=file.Tag.Pictures.FirstOrDefault();
                        if(picture!=null && picture.Data.Count is >0 and <20000000)
                        { await File.WriteAllBytesAsync(cacheFile,picture.Data.Data,token); cover=cacheFile; break; }
                    }
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException) { }
                }
            }
            if(cover!=null) foreach(var (_,index) in group) result[index]=result[index] with {CoverPath=cover};
            progress?.Report(++completed);
        }
        return result;
    }
}
