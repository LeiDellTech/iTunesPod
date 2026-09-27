using System.Security.Cryptography;
using iTunesPod.Core;

namespace iTunesPod.Infrastructure;

public sealed record CloudTransferProgress(string Id, string State, long Bytes = 0, long? Total = null);
public sealed record CloudImportResult(Playlist? Playlist, int Imported, int Reused, IReadOnlyList<string> Errors);

public sealed class CloudMusicImporter : IDisposable
{
    readonly LibraryStore _store;
    readonly CloudMusicClient _api;
    readonly HttpClient _media;
    readonly bool _ownsMedia;
    public CloudMusicImporter(LibraryStore store, CloudMusicClient api, HttpClient? media = null)
    {
        _store = store; _api = api; _ownsMedia = media == null;
        _media = media ?? new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    }
    public void Dispose() { if (_ownsMedia) _media.Dispose(); }
    public static string MappingKey(string id) => "cloud-track:netease:" + CloudMusicClient.ParseId(id);
    public static string PlaylistKey(string collectionId) => "cloud-playlist:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(collectionId)))[..24];
    public async Task<CloudImportResult> ImportAsync(IReadOnlyList<CloudSong> collection, IReadOnlySet<string> selected, string collectionId, string name, CloudMusicSource source, IProgress<CloudTransferProgress>? progress, CancellationToken token)
    {
        var imported = 0; var reused = 0; var errors = new List<string>(); Playlist? local = null;
        var trackMap = _store.LoadTracks().ToDictionary(x => x.Id);
        var mappings = collection.DistinctBy(x => x.Id).ToDictionary(x => x.Id, x => _store.GetPreference(MappingKey(x.Id)));
        void SaveList()
        {
            var ids = collection.Select(x => mappings[x.Id]).Where(id => trackMap.TryGetValue(id, out var track) && File.Exists(track.Path)).Distinct().ToArray();
            if (ids.Length == 0) return;
            local = new(PlaylistKey(collectionId), name, ids); _store.SavePlaylist(local);
        }
        try
        {
            foreach (var song in collection.Where(x => selected.Contains(x.Id)).DistinctBy(x => x.Id))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(new(song.Id, "检查本地文件"));
                    var cachedId = mappings[song.Id];
                    var cached = trackMap.GetValueOrDefault(cachedId);
                    if (cached != null && File.Exists(cached.Path))
                    {
                        await using var stream = File.OpenRead(cached.Path);
                        if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) == cached.Hash) { reused++; progress?.Report(new(song.Id, "已在资料库")); SaveList(); continue; }
                    }
                    if (!song.DetailsAvailable) throw new IOException("歌曲详情不可用，已保留占位但跳过下载。");
                    var link = await _api.ResolveAsync(song, source, token);
                    var folder = Path.Combine(_store.DataDirectory, "online-downloads", "netease", CloudMusicClient.ParseId(song.Id)); Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, "audio" + link.Extension);
                    if (!File.Exists(path)) await Download(song, link, path, progress, token);
                    var result = await new LibraryScanner(_store).ImportAsync([path], null, token);
                    if (result.Errors.Count > 0) throw new IOException("音频未能导入资料库。" + string.Join("；", result.Errors));
                    var track = _store.LoadTracks().FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (track == null)
                    {
                        await using var file = File.OpenRead(path); var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                        track = _store.LoadTracks().FirstOrDefault(x => x.Hash == hash);
                    }
                    if (track == null) throw new IOException("音频未进入资料库，请重试。");
                    _store.SetPreference(MappingKey(song.Id), track.Id); mappings[song.Id] = track.Id; trackMap[track.Id] = track;
                    imported++; SaveList(); progress?.Report(new(song.Id, "已导入资料库"));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
                { errors.Add(song.Title + "：" + ex.Message); progress?.Report(new(song.Id, "未完成：" + ex.Message)); }
            }
        }
        finally { SaveList(); }
        return new(local, imported, reused, errors);
    }
    async Task<HttpResponseMessage> Get(Uri uri, CancellationToken token)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(40));
            HttpResponseMessage response;
            try { response = await _media.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
            catch (HttpRequestException) { throw new IOException("音频服务器连接失败。"); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("音频服务器连接超时。"); }
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
            { var next = new Uri(uri, response.Headers.Location); response.Dispose(); uri = PodcastService.ValidateUrl(next.AbsoluteUri); continue; }
            if (!response.IsSuccessStatusCode) { var status = (int)response.StatusCode; response.Dispose(); throw new IOException($"音频下载失败（HTTP {status}）。"); }
            return response;
        }
        throw new IOException("音频链接重定向次数过多。");
    }
    async Task Download(CloudSong song, CloudAudioLink link, string final, IProgress<CloudTransferProgress>? progress, CancellationToken token)
    {
        const long limit = 512L * 1024 * 1024;
        // Keep a valid media extension so the decoder selects the actual file parser.
        var partial = Path.Combine(Path.GetDirectoryName(final)!, "pending-" + Guid.NewGuid().ToString("N") + link.Extension);
        try
        {
            using var response = await Get(link.Url, token); var length = response.Content.Headers.ContentLength;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) throw new IOException("音频链接返回了错误页面。");
            if (length > limit) throw new IOException("单首音频超过 512 MiB。");
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); downloadTimeout.CancelAfter(TimeSpan.FromMinutes(10));
            var downloadToken = downloadTimeout.Token;
            await using (var input = await response.Content.ReadAsStreamAsync(downloadToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int n; var last = DateTime.MinValue;
                while ((n = await input.ReadAsync(buffer, downloadToken)) > 0)
                {
                    total += n; if (total > limit) throw new IOException("音频超过下载限制。"); await output.WriteAsync(buffer.AsMemory(0, n), downloadToken);
                    if (DateTime.UtcNow - last > TimeSpan.FromMilliseconds(200)) { last = DateTime.UtcNow; progress?.Report(new(song.Id, "下载中", total, length)); }
                }
                if (length.HasValue && total != length) throw new IOException("下载文件不完整，请重试。");
            }
            token.ThrowIfCancellationRequested();
            using (var media = TagLib.File.Create(partial))
            {
                var seconds = media.Properties.Duration.TotalSeconds;
                if (seconds <= 0 || media.Properties.AudioChannels <= 0 || media.Properties.VideoWidth > 0) throw new IOException("下载内容不是有效音频。");
                if (song.Seconds > 45 && seconds < song.Seconds * .8) throw new IOException("返回的音频疑似试听片段，已跳过。");
                media.Tag.Title = song.Title; media.Tag.Performers = song.Artist.Split(" / ", StringSplitOptions.RemoveEmptyEntries); media.Tag.Album = song.Album;
                media.Tag.AlbumArtists = media.Tag.Performers; media.Save();
            }
            if (song.CoverUrl.Length > 0)
            {
                try
                {
                    using var coverTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); coverTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var cover = await Get(PodcastService.ValidateUrl(song.CoverUrl), coverTimeout.Token);
                    await using var input = await cover.Content.ReadAsStreamAsync(coverTimeout.Token); using var image = new MemoryStream(); var data = new byte[81920]; int n;
                    while ((n = await input.ReadAsync(data, coverTimeout.Token)) > 0) { if (image.Length + n > 4 * 1024 * 1024) throw new IOException("封面过大。"); image.Write(data, 0, n); }
                    var raw = image.ToArray(); var jpeg = raw.Length > 2 && raw[0] == 0xff && raw[1] == 0xd8; var png = raw.Length > 8 && raw[0] == 0x89 && raw[1] == 0x50;
                    if (jpeg || png) { using var media = TagLib.File.Create(partial); media.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(raw)) { MimeType = jpeg ? "image/jpeg" : "image/png", Type = TagLib.PictureType.FrontCover }]; media.Save(); }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* Optional artwork timed out. */ }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or NotSupportedException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException) { /* Audio remains usable if optional artwork is unavailable. */ }
            }
            token.ThrowIfCancellationRequested(); File.Move(partial, final);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("音频下载超时，可重试。"); }
        catch (HttpRequestException) { throw new IOException("音频传输中断，可重试。"); }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
