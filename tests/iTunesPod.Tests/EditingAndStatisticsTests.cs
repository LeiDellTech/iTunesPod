using System.Buffers.Binary;
using iTunesPod.Core;
using iTunesPod.Infrastructure;
using static iTunesPod.Infrastructure.ITunesDbWriter;
namespace iTunesPod.Tests;
public sealed class EditingAndStatisticsTests
{
    static readonly byte[] Identity=Convert.FromHexString("0011223344556677");
    static Track Song(string id="source")=>new(id,"song.mp3","","歌曲","歌手","专辑","专辑歌手",2,3,2026,60,100,Genre:"摇滚",Rating:4){Composer="作曲",Compilation=true};
    [Fact] public void MetadataEditingRetainsOpaqueBytesAndIdentity()
    {
        var original=WriterTests.Fixture();var result=DeviceDatabaseEdits.Apply(original,Identity,updates:new Dictionary<uint,Track>{{42,Song("device:42")}});
        var track=Assert.Single(new ITunesDbReader().Read(result,Path.GetTempPath()));Assert.Equal("作曲",track.Composer);Assert.True(track.Compilation);Assert.Equal(4u,track.Rating);Assert.Equal(2u,track.Disc);
        var raw=Children(result,U(result,4)).Single(x=>U(x,12)==1);var at=U(raw,4);var item=Assert.Single(Children(raw,at+U(raw,at+4)));Assert.Equal(0xab,item[500]);Assert.Equal(new byte[]{1,2,3,4,5},Children(item,U(item,4)).Single(x=>U(x,12)==99)[24..]);Assert.True(DeviceChecksum.VerifyHash58(result,Identity));
    }
    [Fact] public void PlaylistsPreserveDuplicatesReorderRenameAndDelete()
    {
        var original=WriterTests.Fixture();var reader=new ITunesDbReader();var master=reader.ReadPlaylists(original);var list=new Playlist("device-playlist:0000000000000008","列表",["device:42","device:42"]);
        var added=DeviceDatabaseEdits.Apply(original,Identity,playlists:master.Append(list).ToArray());Assert.Equal(list.TrackIds,reader.ReadPlaylists(added).Single(x=>!x.IsSystem).TrackIds);
        var renamed=DeviceDatabaseEdits.Apply(added,Identity,playlists:reader.ReadPlaylists(added).Select(x=>x.IsSystem?x:x with{Name="更名",TrackIds=["device:42"]}).ToArray());Assert.Equal("更名",reader.ReadPlaylists(renamed).Single(x=>!x.IsSystem).Name);
        var removed=DeviceDatabaseEdits.Apply(renamed,Identity,playlists:master);Assert.Single(reader.ReadPlaylists(removed));
    }
    [Fact] public void TrackDeletionRemovesAllPlaylistReferences()
    {
        var original=WriterTests.Fixture();var result=DeviceDatabaseEdits.Apply(original,Identity,deleteTracks:new HashSet<uint>{42});var reader=new ITunesDbReader();Assert.Empty(reader.Read(result,Path.GetTempPath()));Assert.Empty(Assert.Single(reader.ReadPlaylists(result)).TrackIds);
    }
    [Fact] public void ClockConversionRetainsUtcAndOpaqueTrackBytes()
    {
        var db=WriterTests.Fixture();BinaryPrimitives.WriteInt32LittleEndian(db.AsSpan(108),-18000);var time=checked((uint)(DateTimeOffset.Parse("2026-09-26T12:00:00Z").ToUnixTimeSeconds()+2082844800-18000));var trackAt=244+96+12;BinaryPrimitives.WriteUInt32LittleEndian(db.AsSpan(trackAt+104),time);db=DeviceChecksum.SignHash58(db,Identity);
        var utc=new ITunesDbReader().Read(db,Path.GetTempPath())[0].AddedUtc;var normalized=DeviceClock.Normalize(db,28800,Identity);Assert.Equal(utc,new ITunesDbReader().Read(normalized,Path.GetTempPath())[0].AddedUtc);Assert.Equal(28800,DeviceClock.DatabaseOffset(normalized));Assert.Equal(0xab,normalized[trackAt+500]);Assert.True(DeviceChecksum.VerifyHash58(normalized,Identity));Assert.Equal(normalized,DeviceClock.Normalize(normalized,28800,Identity));
    }
    [Fact] public void PlayCountsFoldConvertsTimeAndPreservesStats()
    {
        var db=WriterTests.Fixture();var counts=Header("mhdp",96);Array.Resize(ref counts,124);Put(counts,8,28);Put(counts,12,1);Put(counts,96,3);Put(counts,104,12000);Put(counts,108,80);Put(counts,116,2);var utc=DateTimeOffset.Parse("2026-09-26T12:00:00Z");BinaryPrimitives.WriteUInt32LittleEndian(counts.AsSpan(100),checked((uint)(utc.ToUnixTimeSeconds()+2082844800+28800)));
        var result=DeviceStatistics.Fold(db,counts,Identity,28800);var track=Assert.Single(new ITunesDbReader().Read(result,Path.GetTempPath()));Assert.Equal(3u,track.PlayCount);Assert.Equal(2u,track.SkipCount);Assert.Equal(4u,track.Rating);Assert.Equal(12000u,track.BookmarkMilliseconds);Assert.Equal(utc,track.LastPlayedUtc);Assert.True(DeviceChecksum.VerifyHash58(result,Identity));Put(counts,12,2);Assert.Throws<IOException>(()=>DeviceStatistics.Fold(db,counts,Identity));
    }
    [Fact] public void RelativeDateRulesAndDurationLimitRoundTrip()
    {
        var definition=new SmartDefinition(true,[new(16,"在最近天数内","7"),new(60,"等于","1")],"最近添加",2,"分钟");var records=SmartPlaylistBinary.Encode(definition);var parsed=SmartPlaylistBinary.Decode(records[0],records[1]);Assert.NotNull(parsed);Assert.Equal(definition.Rules,parsed.Rules);Assert.Equal("分钟",parsed.LimitUnit);
        var a=Song("a") with{AddedUtc=DateTimeOffset.UtcNow.AddDays(-1)};var b=a with{Id="b",AddedUtc=DateTimeOffset.UtcNow.AddDays(-2)};var old=a with{Id="old",AddedUtc=DateTimeOffset.UtcNow.AddDays(-8)};Assert.Equal(new[]{"a","b"},SmartPlaylists.Evaluate(parsed,[old,b,a]).Select(x=>x.Id));
    }
    [Theory][InlineData("HE-AAC",false)][InlineData("LC",true)] public void AudioProfilesAreChecked(string profile,bool expected)=>Assert.Equal(expected,MediaTools.Compatible(new("aac","",0,0,0,"",0,2,44100,160000,10,profile,160000),false));
    [Fact] public void ExportedLogsRedactPathsDeviceIdsAndUrlTokens(){var text="source C:\\Users\\someone\\song.mp3\nhttps://example.com/feed?token=secret#private\nipod:"+new string('a',64);var result=LogRedactor.Redact(text);Assert.DoesNotContain("someone",result);Assert.DoesNotContain("secret",result);Assert.DoesNotContain(new string('a',64),result);Assert.Contains("example.com/feed",result);}
}
