using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using iTunesPod.Core;
using iTunesPod.Infrastructure;

namespace iTunesPod.Tests;

public sealed class AcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iTunesPod-tests-" + Guid.NewGuid().ToString("N"));
    public AcceptanceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private static Track Song(string id, string path, string artist = "甲") => new(id,path,"hash-"+id,"歌"+id,artist,"同名专辑",artist,1,1,2026,60,100);
    private DeviceSession Session() => new("serial:sample",_root,"测试 iPod","未认证","mount1","generation1",1_000_000_000,2_000_000_000,[],"合成镜像未经实机认证");

    [Fact] public void SameAlbumTitleByDifferentArtistsStaysSeparate()
    { Assert.NotEqual(Song("a","a","甲").AlbumKey,Song("b","b","乙").AlbumKey); }

    [Fact] public void PlanIsDeterministicAndHasNoAutomaticDeletes()
    {
        var first = Song("a", "missing-a"); var second = Song("b", "missing-b");
        var p1 = SyncPlanner.Build(Session(),[first,second]); var p2 = SyncPlanner.Build(Session(),[second,first,first]);
        Assert.Equal(p1.PlanHash,p2.PlanHash); Assert.Equal(2,p1.Operations.Count);
        Assert.DoesNotContain(p1.Operations,x => x.Kind == "删除"); Assert.Contains(p1.Blockers,x=>x.Contains("E_SIGNATURE_UNVERIFIED"));
        Assert.All(p1.Operations,x=>Assert.Equal("冲突",x.Kind));
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("../../outside")]
    [InlineData("C:\\outside")]
    public void DevicePathsCannotEscape(string relative) => Assert.Throws<InvalidDataException>(()=>DeviceAccess.SafePath(_root,relative));

    [Fact] public void ReconnectAndExternalEditsInvalidatePlan()
    {
        var session = Session(); var plan = SyncPlanner.Build(session,[]);
        Assert.Equal("E_DEVICE_CHANGED",SyncPlanner.Validate(plan,session with { MountEpoch="mount2" }));
        Assert.Equal("E_EXTERNAL_CHANGE",SyncPlanner.Validate(plan,session with { Generation="changed" }));
        Assert.Null(SyncPlanner.Validate(plan,session));
    }
    [Fact] public void PlaylistsAndDraftsPersistWithoutChangingTracks()
    {
        var store = new LibraryStore(Path.Combine(_root,"index")); var song = Song("a","source"); store.SaveTracks([song]);
        store.SavePlaylist(new("list","我的列表",[song.Id])); store.SaveDraft("device-a",[song.Id]); store.SaveDraft("device-b",[]);
        var reloaded = new LibraryStore(store.DataDirectory); Assert.Single(reloaded.LoadPlaylists());
        Assert.Single(reloaded.LoadDraft("device-a")); Assert.Empty(reloaded.LoadDraft("device-b"));
        reloaded.DeletePlaylist("list"); Assert.Single(reloaded.LoadTracks());
    }
    [Fact] public async Task ImportReadsRealAudioAndDeduplicatesWithoutChangingSources()
    {
        var audio = Path.Combine(_root,"音乐.wav"); WriteWav(audio); var before = await File.ReadAllBytesAsync(audio);
        var store = new LibraryStore(Path.Combine(_root,"index")); var scanner = new LibraryScanner(store);
        var result = await scanner.ImportAsync([audio],null,CancellationToken.None);
        Assert.Empty(result.Errors); Assert.Single(result.Tracks); Assert.InRange(result.Tracks[0].Seconds,.99,1.01);
        Assert.Equal(before,await File.ReadAllBytesAsync(audio));
        File.Copy(audio,Path.Combine(_root,"重编码并非字节不同.wav"));
        var again = await scanner.ImportAsync([audio,Path.Combine(_root,"重编码并非字节不同.wav")],null,CancellationToken.None);
        Assert.Empty(again.Tracks); Assert.Equal(2,again.Duplicates); Assert.Single(store.LoadTracks());
    }
    [Fact] public async Task CanceledImportDoesNotCommitIndex()
    {
        var store = new LibraryStore(Path.Combine(_root,"index")); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LibraryScanner(store).ImportAsync([_root],null,cancel.Token));
        Assert.Empty(store.LoadTracks());
    }
    [Fact] public async Task BrokenAudioReturnsErrorNotEmptySuccess()
    {
        var audio = Path.Combine(_root,"坏文件.mp3"); await File.WriteAllTextAsync(audio,"not mp3");
        var result = await new LibraryScanner(new LibraryStore(Path.Combine(_root,"index"))).ImportAsync([audio],null,CancellationToken.None);
        Assert.Empty(result.Tracks); Assert.Single(result.Errors);
    }
    [Fact] public async Task ReadAndDiagnosticSnapshotLeaveDeviceBytesUnchanged()
    {
        var device = Path.Combine(_root,"device"); var db = Path.Combine(device,"iPod_Control/iTunes/iTunesDB"); Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        var fixture = DatabaseFixture(); await File.WriteAllBytesAsync(db,fixture);
        var info = Path.Combine(device,"iPod_Control/Device"); Directory.CreateDirectory(info); await File.WriteAllTextAsync(Path.Combine(info,"SysInfo"),"SerialNumber: SYNTHETIC\nModelNumStr: TEST");
        var access = new DeviceAccess(); var session = await access.InspectAsync(device);
        Assert.Single(session.Tracks); Assert.Equal("合成曲目",session.Tracks[0].Title); Assert.Equal("艺术家甲",session.Tracks[0].Artist);
        var snapshot = await access.SnapshotAsync(session,Path.Combine(_root,"host-backups"));
        Assert.True(File.Exists(Path.Combine(snapshot,"manifest.json"))); Assert.Equal(fixture,await File.ReadAllBytesAsync(db));
        await Assert.ThrowsAsync<IOException>(()=>access.SnapshotAsync(session,Path.Combine(device,"backups")));
        await File.WriteAllBytesAsync(db,DatabaseFixture("外部修改"));
        await Assert.ThrowsAsync<IOException>(()=>access.SnapshotAsync(session,Path.Combine(_root,"host-backups")));
    }
    [Theory] [InlineData(0)] [InlineData(11)] [InlineData(40)]
    public void TruncatedDatabaseIsRejected(int length) => Assert.Throws<InvalidDataException>(() => new ITunesDbReader().Read(DatabaseFixture()[..length],_root));
    [Fact] public void OversizedRecordAndUnsafeMediaPathAreRejected()
    {
        var data = DatabaseFixture(); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8,4),uint.MaxValue);
        Assert.Throws<InvalidDataException>(()=>new ITunesDbReader().Read(data,_root));
        Assert.Throws<InvalidDataException>(()=>new ITunesDbReader().Read(DatabaseFixture(path:":..:outside.mp3"),_root));
    }
    private static void WriteWav(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36+16000); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(16000); writer.Write(new byte[16000]);
    }
    // A synthetic protocol fixture, not a captured or certified hardware database.
    private static byte[] DatabaseFixture(string title = "合成曲目", string path = ":iPod_Control:Music:F00:TEST.mp3")
    {
        static void U(byte[] b,int at,int value) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at,4),value);
        static byte[] Chunk(string magic,int header,params byte[][] children)
        {
            var data = new byte[header + children.Sum(x=>x.Length)]; Encoding.ASCII.GetBytes(magic).CopyTo(data,0); U(data,4,header); U(data,8,data.Length);
            var offset = header; foreach(var child in children) { child.CopyTo(data,offset); offset += child.Length; } return data;
        }
        static byte[] String(int type,string value)
        { var raw = Encoding.Unicode.GetBytes(value); var b = Chunk("mhod",40,raw); U(b,4,24); U(b,12,type); U(b,24,1); U(b,28,raw.Length); return b; }
        var item = Chunk("mhit",156,String(1,title),String(2,path),String(3,"合成专辑"),String(4,"艺术家甲")); U(item,12,4); U(item,16,42); U(item,40,60000); U(item,44,1);
        var list = Chunk("mhlt",12,item); U(list,8,1); var section = Chunk("mhsd",96,list); U(section,12,1);
        var db = Chunk("mhbd",104,section); U(db,20,1); return db;
    }
}
