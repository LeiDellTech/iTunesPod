using System.Buffers.Binary;
using System.Text;
using iTunesPod.Core;
using iTunesPod.Infrastructure;
namespace iTunesPod.Tests;
public sealed class WriterTests
{
    static readonly byte[] Identity=Convert.FromHexString("0011223344556677");
    static void U(byte[] b,int at,int value)=>BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at,4),value);
    static byte[] Chunk(string magic,int header,params byte[][] parts){var bytes=new byte[header+parts.Sum(x=>x.Length)];Encoding.ASCII.GetBytes(magic).CopyTo(bytes,0);U(bytes,4,header);U(bytes,8,bytes.Length);var at=header;foreach(var part in parts){part.CopyTo(bytes,at);at+=part.Length;}return bytes;}
    internal static byte[] Fixture(){var opaque=Chunk("mhod",24,[1,2,3,4,5]);U(opaque,12,99);var track=Chunk("mhit",624,opaque);U(track,12,1);U(track,16,42);track[500]=0xab;var tracks=Chunk("mhlt",12,track);U(tracks,8,1);var trackSection=Chunk("mhsd",96,tracks);U(trackSection,12,1);var entry=Chunk("mhip",76);U(entry,24,42);var playlist=Chunk("mhyp",184,entry);U(playlist,16,1);playlist[20]=1;U(playlist,28,7);var lists=Chunk("mhlp",12,playlist);U(lists,8,1);var playlistSection=Chunk("mhsd",96,lists);U(playlistSection,12,2);var db=Chunk("mhbd",244,trackSection,playlistSection);U(db,16,115);U(db,20,2);return DeviceChecksum.SignHash58(db,Identity);}
    [Fact] public void MatchesIndependentHash58Vector(){var db=Chunk("mhbd",244);U(db,16,115);Encoding.ASCII.GetBytes("ABCDEFGH").CopyTo(db,24);for(var i=0;i<20;i++)db[50+i]=(byte)i;Assert.Equal("D4D789B629965EE0D40E218C26A33460B05B8A33",Convert.ToHexString(DeviceChecksum.Hash58(db,Identity)));}
    [Fact] public void SigningDoesNotMutateInputAndDetectsChangedPayload(){var original=Fixture();var snapshot=(byte[])original.Clone();var signed=DeviceChecksum.SignHash58(original,Identity);Assert.Equal(snapshot,original);Assert.True(DeviceChecksum.VerifyHash58(signed,Identity));signed[^1]^=1;Assert.False(DeviceChecksum.VerifyHash58(signed,Identity));}
    [Fact] public void NoOpIsByteExact(){var original=Fixture();Assert.Equal(original,new ITunesDbWriter().Build(original,[],Identity));}
    [Fact] public void AppendRetainsExistingOpaqueTrackAndUpdatesMaster(){var original=Fixture();var t=new Track("source","C:\\song.mp3","","新曲","歌手","专辑","歌手",1,2,2026,120,100,SampleRate:44100);var output=new ITunesDbWriter().Build(original,[new(43,t,"iPod_Control/Music/F00/song.mp3")],Identity);var reader=new ITunesDbReader();var tracks=reader.Read(output,Path.GetTempPath());Assert.Equal(2,tracks.Count);Assert.Equal("新曲",tracks[1].Title);Assert.Equal(new[]{"device:42","device:43"},Assert.Single(reader.ReadPlaylists(output)).TrackIds);var offset=244+96+12;Assert.Equal(original.AsSpan(offset,ITunesDbWriterLength(original,offset)).ToArray(),output.AsSpan(offset,ITunesDbWriterLength(original,offset)).ToArray());Assert.True(DeviceChecksum.VerifyHash58(output,Identity));}
    static int ITunesDbWriterLength(byte[] bytes,int offset)=>BitConverter.ToInt32(bytes,offset+8);
    [Fact] public void RefusesCollidingIds(){var t=new Track("x","x","","x","x","x","x",1,1,2026,1,1);Assert.Throws<InvalidDataException>(()=>new ITunesDbWriter().Build(Fixture(),[new(42,t,"iPod_Control/Music/F00/x.mp3")],Identity));}
    [Theory][InlineData("h264","High",30,false)][InlineData("h264","Constrained Baseline",31,false)][InlineData("h264","Constrained Baseline",30,true)]
    public void VideoCompatibilityUsesCodecProfileAndLevel(string codec,string profile,int level,bool expected)=>Assert.Equal(expected,MediaTools.Compatible(new("aac",codec,640,480,30,profile,level,2,44100,1_600_000,10),true));
}
