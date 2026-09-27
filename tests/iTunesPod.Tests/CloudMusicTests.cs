using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using iTunesPod.Infrastructure;

namespace iTunesPod.Tests;

public sealed class CloudMusicTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "iTunesPod-cloud-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    static async Task<Dictionary<string, string>> Form(HttpRequestMessage request)
    {
        var text = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
        return text.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')), x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
    }
    static object Song(string id) => new { id, name = "歌曲" + id, dt = 1000, ar = new[] { new { name = "测试艺术家" } }, al = new { name = "测试专辑", picUrl = "" } };
    [Theory]
    [InlineData("12345", "12345")]
    [InlineData("https://music.163.com/#/playlist?id=12345", "12345")]
    [InlineData("https://music.163.com/user/home?id=12345&other=1", "12345")]
    public void ParsesOnlySupportedIds(string input, string expected) => Assert.Equal(expected, CloudMusicClient.ParseId(input));
    [Theory]
    [InlineData("../../outside")]
    [InlineData("https://music.163.com.attacker.test/playlist?id=12345")]
    [InlineData("https://example.org/?id=12345")]
    public void RejectsUnsafeIds(string input) => Assert.Throws<IOException>(() => CloudMusicClient.ParseId(input));
    [Fact] public async Task AccountCookieNeverReachesGdOrRequestUrls()
    {
        const string session = "MUSIC_U=synthetic-test-value"; var accountCalls = 0; var gdCalls = 0;
        using var account = Http(async (request, _) =>
        {
            accountCalls++; Assert.Equal("api.leidell.cn", request.RequestUri!.Host); Assert.DoesNotContain("synthetic-test-value", request.RequestUri.ToString());
            Assert.False(request.Headers.Contains("Cookie")); var form = await Form(request); Assert.Equal(session, form["cookie"]);
            return Json(new { code = 200, result = new { songs = new[] { Song("1") } } });
        });
        using var gd = Http((request, _) =>
        {
            gdCalls++; Assert.Equal("music-api.gdstudio.xyz", request.RequestUri!.Host); Assert.False(request.Headers.Contains("Cookie")); Assert.Null(request.Content);
            Assert.DoesNotContain("synthetic-test-value", request.RequestUri.ToString());
            return Task.FromResult(Json(new[] { new { id = "1", name = "测试", artist = new[] { "作者" }, album = "专辑" } }));
        });
        using var api = new CloudMusicClient(account, gd); api.SetSession(session);
        Assert.Single(await api.SearchAsync("测试", CloudMusicSource.Account, 1, default));
        Assert.Single(await api.SearchAsync("测试", CloudMusicSource.GdStudio, 1, default));
        api.Logout(); Assert.False(api.HasSession); Assert.Equal(1, accountCalls); Assert.Equal(1, gdCalls);
    }
    [Fact] public async Task QrUsesDocumentedStatesAndReceivesSessionOnlyAt803()
    {
        using var account = Http((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/login/qr/key" => Json(new { code = 200, data = new { unikey = "synthetic-key" } }),
            "/login/qr/create" => Json(new { code = 200, data = new { qrimg = "data:image/png;base64,AQID" } }),
            _ => Json(new { code = 803, cookie = "MUSIC_U=synthetic-test-value" })
        }));
        using var gd = Http((_, _) => throw new InvalidOperationException()); using var api = new CloudMusicClient(account, gd);
        var qr = await api.CreateQrAsync(default); Assert.Equal("synthetic-key", qr.Key);
        var status = await api.CheckQrAsync(qr.Key, default); Assert.Equal(803, status.Code); Assert.NotEmpty(status.Cookie);
    }
    [Fact] public async Task PlaylistBeyond200UsesAllIdsAndRestoresOriginalOrder()
    {
        var ids = Enumerable.Range(1, 251).Select(x => x.ToString()).ToArray(); var batches = 0; var urls = new HashSet<string>();
        using var account = Http(async (request, _) =>
        {
            Assert.True(urls.Add(request.RequestUri!.AbsoluteUri));
            if (request.RequestUri.AbsolutePath == "/playlist/detail") return Json(new { code = 200, playlist = new { name = "全部歌曲", trackCount = 251, tracks = new[] { Song("1") }, trackIds = ids.Select(id => new { id }).ToArray() } });
            batches++; var form = await Form(request); return Json(new { code = 200, songs = form["ids"].Split(',').Reverse().Select(Song).ToArray() });
        });
        using var gd = Http((_, _) => throw new InvalidOperationException()); using var api = new CloudMusicClient(account, gd);
        var result = await api.PlaylistAsync("1", default); Assert.Equal(ids, result.Songs.Select(x => x.Id)); Assert.Equal(3, batches);
    }
    [Fact] public async Task LikesUsesAllSongIds()
    {
        using var account = Http((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/likelist" ? Json(new { code = 200, ids = new[] { 3, 1, 2 } }) : Json(new { code = 200, songs = new[] { Song("1"), Song("2"), Song("3") } })));
        using var gd = Http((_, _) => throw new InvalidOperationException()); using var api = new CloudMusicClient(account, gd);
        Assert.Equal(new[] { "3", "1", "2" }, (await api.LikesAsync("123", default)).Select(x => x.Id));
    }
    [Fact] public async Task UnavailableSongRetainsItsPositionInCollection()
    {
        using var account = Http((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/likelist" ? Json(new { code = 200, ids = new[] { 1, 2, 3 } }) : Json(new { code = 200, songs = new[] { Song("1"), Song("3") } })));
        using var gd = Http((_, _) => throw new InvalidOperationException()); using var api = new CloudMusicClient(account, gd);
        var songs = await api.LikesAsync("123", default); Assert.Equal(new[] { "1", "2", "3" }, songs.Select(x => x.Id)); Assert.False(songs[1].DetailsAvailable);
    }
    [Fact] public async Task PlaylistPaginationContinuesUntilMoreIsFalse()
    {
        using var account = Http(async (request, _) => { var fields = await Form(request); var second = fields["offset"] == "100"; return Json(new { code = 200, more = !second, playlist = new[] { new { id = second ? "2" : "1", name = "歌单", trackCount = 10 } } }); });
        using var gd = Http((_, _) => throw new InvalidOperationException()); using var api = new CloudMusicClient(account, gd);
        Assert.Equal(new[] { "1", "2" }, (await api.PlaylistsAsync("123", default)).Select(x => x.Id));
    }
    [Fact] public async Task UserIdLookupReadsProfileWithoutCreatingLoginSession()
    {
        using var account = Http(async (request, _) =>
        {
            var fields = await Form(request); Assert.Equal("123", fields["uid"]); Assert.False(fields.ContainsKey("cookie"));
            return request.RequestUri!.AbsolutePath == "/user/detail" ? Json(new { code = 200, profile = new { userId = 123, nickname = "测试账号" } }) : Json(new { code = 200, more = false, playlist = new[] { new { id = "1", name = "喜欢的音乐", trackCount = 5, specialType = 5 } } });
        });
        using var gd = Http((_, _) => throw new InvalidOperationException()); using var api = new CloudMusicClient(account, gd);
        var user = await api.UserAsync("https://music.163.com/#/user/home?id=123", default);
        Assert.Equal("测试账号", user.Name); Assert.False(api.HasSession); Assert.True(Assert.Single(await api.PlaylistsAsync(user.Id, default)).IsLiked);
    }
    [Fact] public async Task TrialUrlIsRejectedWithoutAutomaticFallback()
    {
        using var account = Http((_, _) => Task.FromResult(Json(new { code = 200, data = new[] { new { id = "1", url = "https://media.test/1.mp3", type = "mp3", freeTrialInfo = new { start = 0, end = 30 } } } })));
        using var gd = Http((_, _) => throw new InvalidOperationException("must not fall back")); using var api = new CloudMusicClient(account, gd);
        var error = await Assert.ThrowsAsync<IOException>(() => api.ResolveAsync(new("1", "测试", "作者", "专辑", 60), CloudMusicSource.Account, default)); Assert.Contains("试听", error.Message);
    }
    [Fact] public async Task DownloadsImportTagsKeepOrderAndReuseVerifiedFiles()
    {
        var calls = 0;
        using var account = Http(async (request, _) => { var form = await Form(request); return Json(new { code = 200, data = new[] { new { id = form["id"], url = "https://media.test/" + form["id"] + ".wav", type = "wav" } } }); });
        using var gd = Http((_, _) => throw new InvalidOperationException()); using var api = new CloudMusicClient(account, gd); api.SetSession("MUSIC_U=synthetic-test-value");
        using var media = Http((request, _) =>
        {
            Assert.False(request.Headers.Contains("Cookie")); Assert.Null(request.Content); calls++;
            if (request.RequestUri!.AbsolutePath == "/2.wav") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>unavailable</html>", Encoding.UTF8, "text/html") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) });
        });
        var store = new LibraryStore(_root); using var importer = new CloudMusicImporter(store, api, media);
        var songs = Enumerable.Range(1, 3).Select(x => new CloudSong(x.ToString(), "测试" + x, "作者", "专辑", 1)).ToArray(); var selected = songs.Select(x => x.Id).ToHashSet();
        var result = await importer.ImportAsync(songs, selected, "fixture", "测试歌单", CloudMusicSource.Account, null, default);
        Assert.Equal(2, result.Imported); Assert.Single(result.Errors); Assert.NotNull(result.Playlist);
        var tracks = store.LoadTracks().ToDictionary(x => x.Id); Assert.Equal(new[] { "测试1", "测试3" }, result.Playlist!.TrackIds.Select(id => tracks[id].Title));
        Assert.All(tracks.Values, t => { Assert.Equal("作者", t.Artist); Assert.Equal("专辑", t.Album); Assert.True(t.Seconds > .9); });
        var retry = await importer.ImportAsync(songs, selected, "fixture", "测试歌单", CloudMusicSource.Account, null, default);
        Assert.Equal(2, retry.Reused); Assert.Equal(2, store.LoadTracks().Count); Assert.Equal(4, calls);
        Assert.DoesNotContain(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), p => Path.GetFileName(p).StartsWith("pending-"));
    }
    sealed class CancelStream(byte[] bytes, CancellationTokenSource operation) : MemoryStream(bytes)
    {
        int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (++_reads > 1) { operation.Cancel(); token.ThrowIfCancellationRequested(); }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 64)], token);
        }
    }
    [Fact] public async Task CancellationDuringStreamingRemovesIncompleteFile()
    {
        using var operation = new CancellationTokenSource();
        using var account = Http((_, _) => Task.FromResult(Json(new { code = 200, data = new[] { new { id = "1", url = "https://media.test/1.wav", type = "wav" } } })));
        using var gd = Http((_, _) => throw new InvalidOperationException()); using var api = new CloudMusicClient(account, gd);
        using var media = Http((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelStream(Wave(), operation)) }));
        var store = new LibraryStore(_root); using var importer = new CloudMusicImporter(store, api, media);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync([new("1", "测试", "作者", "专辑", 1)], new HashSet<string> { "1" }, "fixture", "列表", CloudMusicSource.Account, null, operation.Token));
        Assert.Empty(store.LoadTracks()); Assert.Empty(store.LoadPlaylists());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "online-downloads"), "*", SearchOption.AllDirectories));
    }
    static byte[] Wave()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream); var pcm = new byte[44100 * 2];
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16); w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm); return stream.ToArray();
    }
}
