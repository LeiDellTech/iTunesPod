using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace iTunesPod.Infrastructure;

public enum CloudMusicSource { Account, GdStudio }
public sealed record CloudSong(string Id, string Title, string Artist, string Album, double Seconds, string CoverUrl = "")
{ public bool DetailsAvailable { get; init; } = true; }
public sealed record CloudPlaylist(string Id, string Name, int Count)
{ public bool IsLiked { get; init; } }
public sealed record CloudAccount(string Id, string Name);
public sealed record CloudQr(string Key, string Image);
public sealed record CloudQrStatus(int Code, string Cookie);
public sealed record CloudAudioLink(Uri Url, string Extension);

/// <summary>Account requests and public GD requests use separate transports; credentials never enter URLs.</summary>
public sealed class CloudMusicClient : IDisposable
{
    readonly HttpClient _account, _public;
    readonly bool _ownsAccount, _ownsPublic;
    readonly Uri _base;
    string _cookie = "";
    public CloudMusicClient(HttpClient? account = null, HttpClient? publicClient = null, Uri? apiBase = null)
    {
        _ownsAccount = account == null; _ownsPublic = publicClient == null;
        _account = account ?? NewClient(); _public = publicClient ?? NewClient();
        _base = apiBase ?? new Uri("https://api.leidell.cn/");
    }
    static HttpClient NewClient() => new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) };
    public bool HasSession => _cookie.Length > 0;
    public void SetSession(string cookie) { if (cookie.Length > 32000 || cookie.Contains('\r') || cookie.Contains('\n')) throw new IOException("登录凭据格式无效。"); _cookie = cookie; }
    public void Logout() => _cookie = "";
    public void Dispose() { Logout(); if (_ownsAccount) _account.Dispose(); if (_ownsPublic) _public.Dispose(); }
    static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
    static long Number(JsonElement e, string key) => long.TryParse(Text(e, key), out var n) ? n : 0;
    static JsonElement Field(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    static IEnumerable<JsonElement> Array(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    public static string ParseId(string input)
    {
        input = input.Trim();
        if (Regex.IsMatch(input, "^[0-9]{1,20}$")) return input;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && (uri.Host == "music.163.com" || uri.Host.EndsWith(".music.163.com", StringComparison.OrdinalIgnoreCase)))
        {
            var match = Regex.Match(uri.Query + uri.Fragment, "(?:[?&])id=([0-9]{1,20})(?:[&#]|$)");
            if (match.Success) return match.Groups[1].Value;
        }
        throw new IOException("请输入数字 ID，或网易云音乐的用户 / 歌单链接。");
    }
    async Task<JsonElement> Request(string route, Dictionary<string, string>? fields, CancellationToken token)
    {
        fields = fields == null ? [] : new(fields);
        fields["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        if (_cookie.Length > 0) fields["cookie"] = _cookie;
        // Some deployed NCM APIs cache POST responses by URL rather than form body.
        // Keep each batch/QR poll distinct while retaining all credentials in the body.
        var endpoint = new Uri(_base, route + "?timestamp=" + fields["timestamp"] + "&_itp=" + Guid.NewGuid().ToString("N"));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await Send(_account, request, token);
    }
    async Task<JsonElement> Gd(Dictionary<string, string> fields, CancellationToken token)
    {
        fields["source"] = "netease";
        var query = string.Join("&", fields.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://music-api.gdstudio.xyz/api.php?" + query);
        return await Send(_public, request, token);
    }
    static async Task<JsonElement> Send(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new IOException($"音乐服务暂时不可用（HTTP {(int)response.StatusCode}）。");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream(); var bytes = new byte[81920]; int n;
            while ((n = await stream.ReadAsync(bytes, token)) > 0) { if (buffer.Length + n > 8 * 1024 * 1024) throw new IOException("音乐服务返回的数据过大。"); await buffer.WriteAsync(bytes.AsMemory(0, n), token); }
            using var doc = JsonDocument.Parse(buffer.ToArray()); var root = doc.RootElement;
            var code = Number(root, "code");
            if (code != 0 && code != 200 && code is not (800 or 801 or 802 or 803))
                throw new IOException(code is 301 or 302 ? "登录已失效，请重新扫码登录。" : $"音乐服务未完成请求（状态 {code}）。");
            return root.Clone();
        }
        catch (JsonException) { throw new IOException("音乐服务没有返回有效数据，请稍后重试。"); }
        catch (HttpRequestException) { throw new IOException("无法连接音乐服务，请检查网络后重试。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("音乐服务连接超时，请稍后重试。"); }
    }
    public async Task<CloudQr> CreateQrAsync(CancellationToken token)
    {
        var key = Text(Field(await Request("login/qr/key", null, token), "data"), "unikey");
        if (key.Length == 0) throw new IOException("无法获取登录二维码。");
        var image = Text(Field(await Request("login/qr/create", new() { ["key"] = key, ["qrimg"] = "true" }, token), "data"), "qrimg");
        if (!image.StartsWith("data:image/", StringComparison.Ordinal) || image.Length > 2 * 1024 * 1024) throw new IOException("二维码图片无效。");
        return new(key, image);
    }
    public async Task<CloudQrStatus> CheckQrAsync(string key, CancellationToken token)
    {
        var root = await Request("login/qr/check", new() { ["key"] = key, ["noCookie"] = "true" }, token);
        return new((int)Number(root, "code"), Text(root, "cookie"));
    }
    public async Task<CloudAccount?> AccountAsync(CancellationToken token)
    {
        var data = Field(await Request("login/status", null, token), "data"); var profile = Field(data, "profile");
        var id = Text(profile, "userId");
        return id.Length > 0 ? new(id, Text(profile, "nickname")) : null;
    }
    public async Task<CloudAccount> UserAsync(string userId, CancellationToken token)
    {
        var id = ParseId(userId);
        var profile = Field(await Request("user/detail", new() { ["uid"] = id }, token), "profile");
        if (Text(profile, "userId") != id) throw new IOException("没有找到这个网易云账号，请检查用户 ID。");
        return new(id, Text(profile, "nickname"));
    }
    public async Task<IReadOnlyList<CloudPlaylist>> PlaylistsAsync(string userId, CancellationToken token)
    {
        var results = new List<CloudPlaylist>(); var seen = new HashSet<string>();
        for (var offset = 0; offset < 10000; offset += 100)
        {
            var root = await Request("user/playlist", new() { ["uid"] = ParseId(userId), ["limit"] = "100", ["offset"] = offset.ToString() }, token);
            var items = Array(Field(root, "playlist")).ToArray(); var before = results.Count;
            foreach (var item in items) { var id = Text(item, "id"); if (id.Length > 0 && seen.Add(id)) results.Add(new(id, Text(item, "name"), (int)Number(item, "trackCount")) { IsLiked = Number(item, "specialType") == 5 }); }
            if (Text(root, "more") != "True" && Text(root, "more") != "true") return results;
            if (results.Count == before) throw new IOException("歌单分页没有继续，请重试。");
        }
        throw new IOException("歌单数量超过本次加载上限。");
    }
    public async Task<IReadOnlyList<CloudSong>> LikesAsync(string userId, CancellationToken token)
    {
        var root = await Request("likelist", new() { ["uid"] = ParseId(userId) }, token);
        return await DetailsAsync(Array(Field(root, "ids")).Select(x => x.ToString()).ToArray(), token);
    }
    public async Task<(CloudPlaylist Playlist, IReadOnlyList<CloudSong> Songs)> PlaylistAsync(string id, CancellationToken token)
    {
        var root = Field(await Request("playlist/detail", new() { ["id"] = ParseId(id) }, token), "playlist");
        var ids = Array(Field(root, "trackIds")).Select(x => Text(x, "id")).Where(x => x.Length > 0).ToArray();
        if (ids.Length == 0 && Number(root, "trackCount") > 0) throw new IOException("服务没有返回完整歌单，请扫码登录后重试。");
        return (new(ParseId(id), Text(root, "name"), (int)Number(root, "trackCount")), await DetailsAsync(ids, token));
    }
    async Task<IReadOnlyList<CloudSong>> DetailsAsync(IReadOnlyList<string> ids, CancellationToken token)
    {
        var songs = new Dictionary<string, CloudSong>();
        foreach (var batch in ids.Chunk(100))
        {
            token.ThrowIfCancellationRequested();
            var root = await Request("song/detail", new() { ["ids"] = string.Join(",", batch) }, token);
            foreach (var item in Array(Field(root, "songs"))) { var song = ParseSong(item); songs[song.Id] = song; }
        }
        // Deleted or region-restricted tracks remain visible; never silently shorten a collection.
        return ids.Select(id => songs.TryGetValue(id, out var song) ? song : new CloudSong(id, "歌曲详情不可用 · " + id, "", "", 0) { DetailsAvailable = false }).ToArray();
    }
    static CloudSong ParseSong(JsonElement s)
    {
        var album = Field(s, "al"); if (album.ValueKind != JsonValueKind.Object) album = Field(s, "album");
        var artists = Field(s, "ar"); if (artists.ValueKind != JsonValueKind.Array) artists = Field(s, "artists");
        return new(Text(s, "id"), Text(s, "name"), string.Join(" / ", Array(artists).Select(x => Text(x, "name"))), Text(album, "name"), Math.Max(Number(s, "dt"), Number(s, "duration")) / 1000d, Text(album, "picUrl"));
    }
    public async Task<IReadOnlyList<CloudSong>> SearchAsync(string query, CloudMusicSource source, int page, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (source == CloudMusicSource.Account)
        {
            var root = await Request("cloudsearch", new() { ["keywords"] = query.Trim(), ["type"] = "1", ["limit"] = "50", ["offset"] = ((page - 1) * 50).ToString() }, token);
            return Array(Field(Field(root, "result"), "songs")).Select(ParseSong).ToArray();
        }
        var gd = await Gd(new() { ["types"] = "search", ["name"] = query.Trim(), ["count"] = "50", ["pages"] = page.ToString() }, token);
        if (gd.ValueKind != JsonValueKind.Array) gd = Field(gd, "result");
        return Array(gd).Select(s => new CloudSong(Text(s, "id"), Text(s, "name"), string.Join(" / ", Array(Field(s, "artist")).Select(x => x.ToString())), Text(s, "album"), 0)).ToArray();
    }
    public async Task<CloudAudioLink> ResolveAsync(CloudSong song, CloudMusicSource source, CancellationToken token)
    {
        JsonElement data;
        if (source == CloudMusicSource.Account)
        {
            var root = await Request("song/url/v1", new() { ["id"] = ParseId(song.Id), ["level"] = "exhigh" }, token);
            data = Array(Field(root, "data")).FirstOrDefault(s => Text(s, "id") == song.Id);
            if (Field(data, "freeTrialInfo").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) throw new IOException("仅提供试听片段，已跳过；请检查账号下载权限。");
        }
        else data = await Gd(new() { ["types"] = "url", ["id"] = ParseId(song.Id), ["br"] = "320" }, token);
        var url = Text(data, "url");
        if (url.Length == 0) throw new IOException("当前来源没有提供可用音频链接，已跳过。");
        var uri = PodcastService.ValidateUrl(url); var extension = Text(data, "type").ToLowerInvariant();
        if (extension.Length > 0 && !extension.StartsWith('.')) extension = "." + extension;
        if (extension.Length == 0) extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".mp3" or ".m4a" or ".flac" or ".wav" or ".ogg" or ".aac")) extension = ".mp3";
        return new(uri, extension);
    }
}
