using iTunesPod.Infrastructure;

namespace iTunesPod.App;

public sealed partial class MainWindow
{
    async Task ValidateCloudPage(string output)
    {
        var width = Width; var height = Height; var dark = _dark;
        Navigate("网易云音乐");
        if (_args.Contains("--cloud-api-preview"))
        {
            await CloudRun(t => LoadCloudPlaylist(new CloudPlaylist("3778678", "热歌榜（公开 API 检查）", 200), t));
            if (_cloudRows.Count == 0) throw new System.IO.IOException("公开歌单界面检查未加载任何歌曲。");
            _cloudPlaylists = [new("3778678", "热歌榜（公开 API 检查）", _cloudRows.Count)];
            if (_cloudPlaylistList != null) _cloudPlaylistList.ItemsSource = _cloudPlaylists;
        }
        await Shot(output, "cloud-music-light");
        _dark = true; ApplyTheme(); Navigate("网易云音乐"); await Shot(output, "cloud-music-dark");
        Width = 1060; Height = 760; Navigate("网易云音乐"); await Shot(output, "cloud-music-compact");
        Width = width; Height = height; _dark = dark; ApplyTheme();
    }
}
