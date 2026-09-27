using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using iTunesPod.Infrastructure;
namespace iTunesPod.Tests;
public class PlaylistReaderTests
{
    static void U(byte[] b,int at,int value)=>BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at,4),value);
    static byte[] Chunk(string magic,int header,params byte[][] children){var data=new byte[header+children.Sum(x=>x.Length)];Encoding.ASCII.GetBytes(magic).CopyTo(data,0);U(data,4,header);U(data,8,data.Length);var at=header;foreach(var child in children){child.CopyTo(data,at);at+=child.Length;}return data;}
    static byte[] Fixture(){var raw=Encoding.Unicode.GetBytes("我的收藏");var name=Chunk("mhod",40,raw);U(name,4,24);U(name,12,1);U(name,24,1);U(name,28,raw.Length);var entry=Chunk("mhip",32);U(entry,24,42);var list=Chunk("mhyp",108,name,entry,entry);U(list,12,1);U(list,16,2);U(list,28,13);var lists=Chunk("mhlp",12,list);U(lists,8,1);var section=Chunk("mhsd",96,lists);U(section,12,2);var db=Chunk("mhbd",104,section);U(db,20,1);return db;}
    [Fact] public void ReadsUnicodeNameAndPreservesRepeatedEntries(){var result=new ITunesDbReader().ReadPlaylists(Fixture());Assert.Single(result);Assert.Equal("我的收藏",result[0].Name);Assert.Equal(new[]{"device:42","device:42"},result[0].TrackIds);}
    [Fact] public void ReadsCompressedPlaylistDatabase(){var source=Fixture();using var target=new MemoryStream();target.Write(source,0,104);using(var zip=new ZLibStream(target,CompressionLevel.SmallestSize,true))zip.Write(source,104,source.Length-104);var compressed=target.ToArray();U(compressed,8,compressed.Length);U(compressed,12,2);Assert.Equal("我的收藏",Assert.Single(new ITunesDbReader().ReadPlaylists(compressed)).Name);}
    [Fact] public void RejectsTruncatedPlaylistRecord(){var data=Fixture();U(data,8,data.Length-1);Assert.Throws<InvalidDataException>(()=>new ITunesDbReader().ReadPlaylists(data[..^1]));}
}
