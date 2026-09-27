using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using iTunesPod.Core;
using iTunesPod.Infrastructure;

namespace iTunesPod.App;

public sealed partial class MainWindow
{
    readonly CloudMusicClient _cloudApi = new();
    readonly ObservableCollection<CloudRow> _cloudRows = [];
    IReadOnlyList<CloudPlaylist> _cloudPlaylists = [];
    CloudAccount? _cloudAccount;
    CloudAccount? _cloudPublicUser;
    Playlist? _cloudLocalPlaylist;
    CancellationTokenSource? _cloudOperation;
    bool _cloudBusy;
    string _cloudCollectionId = "", _cloudCollectionName = "", _cloudQuery = "", _cloudMessage = "扫码登录获取收藏，或粘贴公开歌单链接。";
    string _cloudCollectionSummary = "";
    int _cloudSearchPage = 1;
    CloudMusicSource _cloudSource;
    DataGrid? _cloudTable;
    ListBox? _cloudPlaylistList;
    FrameworkElement? _cloudSourcePicker;
    TextBox? _cloudSearchBox;
    TextBox? _cloudUserBox;
    TextBlock? _cloudAccountLabel, _cloudProgressLabel;
    ProgressBar? _cloudProgressBar;
    Button? _cloudCancel, _cloudSync;
    FrameworkElement? _cloudEmpty;
    readonly List<Button> _cloudCommands = [];

    sealed class CloudRow(CloudSong song) : INotifyPropertyChanged
    {
        public CloudSong Song { get; } = song;
        public string Title => Song.Title;
        public string Artist => Song.Artist;
        public string Album => Song.Album;
        public string Duration => Song.Seconds > 0 ? Time(Song.Seconds) : "—";
        string _state = "待下载";
        public string State { get => _state; set { _state = value; PropertyChanged?.Invoke(this, new(nameof(State))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    void CloudMusic()
    {
        _subtitle.Text = _cloudCollectionName.Length > 0 ? _cloudCollectionName + " · " + _cloudRows.Count + " 个曲目" : "你的收藏，从云端到电脑，再到 iPod"; _cloudCommands.Clear();
        Button Command(string label, Func<Task> task, string? icon = null, bool primary = false)
        { var button = Action(label, task, icon, primary); _cloudCommands.Add(button); return button; }
        var root = Grid("*", "Auto,Auto,*,Auto");
        _cloudAccountLabel = T("", 13);
        var account = new WrapPanel();
        foreach (var element in new UIElement[] { _cloudAccountLabel, Command("通过 ID 导入", () => CloudRun(PublicCloudUser), "person", true), Command("扫码登录", () => CloudRun(LoginCloud), "person"), Command("使用 Cookie", () => CloudRun(CookieCloud)), Command("退出登录", LogoutCloud), Command("打开歌单链接", () => CloudRun(OpenCloudPlaylist), "list") }) { if (element is FrameworkElement f) f.Margin = new(0, 0, 10, 10); account.Children.Add(element); }
        root.Children.Add(account);
        var accountSource = Action("网易云", () => Task.CompletedTask); var gdSource = Action("GD Studio", () => Task.CompletedTask);
        void SourcePaint() { accountSource.Background = _cloudSource == CloudMusicSource.Account ? B("Selection") : Brushes.Transparent; gdSource.Background = _cloudSource == CloudMusicSource.GdStudio ? B("Selection") : Brushes.Transparent; }
        accountSource.ToolTip = "搜索与下载使用网易云账号接口"; gdSource.ToolTip = "搜索与下载使用 GD Studio 的网易云来源";
        accountSource.Click += (_, _) => { _cloudSource = CloudMusicSource.Account; SourcePaint(); }; gdSource.Click += (_, _) => { _cloudSource = CloudMusicSource.GdStudio; SourcePaint(); };
        SourcePaint(); _cloudSourcePicker = Row(accountSource, gdSource); _cloudSourcePicker.Margin = new(0, 0, 12, 0);
        _cloudSearchBox = new TextBox { Text = _cloudQuery, MinWidth = 150, ToolTip = "搜索歌曲、艺术家或专辑", Margin = new(0, 0, 12, 0) };
        var search = Grid("Auto,*,Auto,Auto,Auto", "*"); search.Margin = new(0, 0, 0, 16); search.Children.Add(_cloudSourcePicker); Col(_cloudSearchBox, 1); search.Children.Add(_cloudSearchBox);
        var find = Command("搜索", () => CloudRun(t => SearchCloud(1, t)), "search"); Col(find, 2); search.Children.Add(find);
        var previous = Command("上一页", () => CloudRun(t => SearchCloud(Math.Max(1, _cloudSearchPage - 1), t))); Col(previous, 3); search.Children.Add(previous);
        var next = Command("下一页", () => CloudRun(t => SearchCloud(_cloudSearchPage + 1, t))); Col(next, 4); search.Children.Add(next);
        _cloudSearchBox.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await Guard(() => CloudRun(t => SearchCloud(1, t))); } };
        At(search, 1); root.Children.Add(search);
        var content = Grid("225,*", "*");
        _cloudPlaylistList = new ListBox { ItemsSource = _cloudPlaylists, DisplayMemberPath = "Name", BorderThickness = new(0), Background = B("Panel"), Foreground = B("Ink"), Margin = new(0, 0, 18, 0) };
        _cloudPlaylistList.SelectionChanged += async (_, _) => { if (!_cloudBusy && _cloudPlaylistList.SelectedItem is CloudPlaylist list) await Guard(() => CloudRun(t => LoadCloudPlaylist(list, t))); };
        var lists = Grid("*", "Auto,*"); lists.Margin = new(0, 0, 18, 0);
        var listHeading = T("收藏歌单", 14); listHeading.Margin = new(10, 0, 0, 12); lists.Children.Add(listHeading);
        _cloudPlaylistList.Margin = new(0); At(_cloudPlaylistList, 1); lists.Children.Add(_cloudPlaylistList); content.Children.Add(lists);
        _cloudTable = new DataGrid { ItemsSource = _cloudRows, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow, EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column, BorderThickness = new(0), RowHeight = 42 };
        foreach (var (heading, property, width) in new[] { ("歌曲", "Title", 2d), ("艺术家", "Artist", 1.3), ("专辑", "Album", 1.3), ("时长", "Duration", .55), ("下载状态", "State", 1.7) })
        {
            var style = new Style(typeof(TextBlock), (Style)Resources[typeof(TextBlock)]);
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
            _cloudTable.Columns.Add(new DataGridTextColumn { Header = heading, Binding = new Binding(property), ElementStyle = style, Width = property == "Duration" ? new DataGridLength(58) : new DataGridLength(width, DataGridLengthUnitType.Star) });
        }
        var albumColumn = _cloudTable.Columns[2]; _cloudTable.SizeChanged += (_, e) => albumColumn.Visibility = e.NewSize.Width < 620 ? Visibility.Collapsed : Visibility.Visible;
        Col(_cloudTable, 1); content.Children.Add(_cloudTable);
        _cloudUserBox = new TextBox { Text = _cloudPublicUser?.Id ?? "", MaxLength = 500, MinHeight = 34, ToolTip = "例如数字用户 ID，或 https://music.163.com/#/user/home?id=…" };
        _cloudUserBox.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await Guard(() => CloudRun(t => LookupCloudUser(_cloudUserBox.Text, t))); } };
        _cloudEmpty = new Border { MaxWidth = 490, Padding = new(24), Background = B("Canvas"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Child = Stack(T("通过 ID，找到你的音乐", 25), T("输入网易云用户 ID 或个人主页链接，读取公开收藏歌单。私密收藏需要扫码登录。", 13, true), T("用户 ID / 个人主页链接", 12, true), _cloudUserBox, Row(Command("查找账号并读取歌单", () => CloudRun(t => LookupCloudUser(_cloudUserBox.Text, t)), "person", true), Command("扫码登录", () => CloudRun(LoginCloud), "person"))) };
        foreach (var child in ((StackPanel)((Border)_cloudEmpty).Child).Children.OfType<FrameworkElement>().Skip(1)) child.Margin = new(0, 0, 0, 16);
        Col(_cloudEmpty, 1); content.Children.Add(_cloudEmpty); At(content, 2); root.Children.Add(content);
        _cloudProgressLabel = T(_cloudMessage, 12, true); _cloudProgressLabel.MaxHeight = 50;
        _cloudProgressBar = new ProgressBar { Height = 3, Minimum = 0, Maximum = 100, Foreground = B("Accent"), Background = B("Panel"), Margin = new(0, 10, 0, 12) };
        _cloudCancel = Action("取消任务", () => { _cloudOperation?.Cancel(); return Task.CompletedTask; });
        _cloudSync = Command("同步本地歌单到 iPod", () => _cloudLocalPlaylist == null ? Task.CompletedTask : SyncPlaylist(_cloudLocalPlaylist), "usb");
        var actions = new WrapPanel();
        foreach (var button in new[] { Command("下载选中到资料库", () => DownloadCloud(false), "export", true), Command("下载全部", () => DownloadCloud(true)), Command("查看本地歌单", () => Go("播放列表"), "list"), _cloudSync, _cloudCancel }) { button.Margin = new(0, 8, 9, 8); actions.Children.Add(button); }
        var footer = Stack(_cloudProgressBar, _cloudProgressLabel, actions, T("支持 Ctrl / Shift 多选。下载成功的歌曲会组成本地歌单；同步到 iPod 时会再次预览并确认。", 11, true));
        footer.Margin = new(0, 12, 0, 0); At(footer, 3); root.Children.Add(footer); _page.Content = root;
        if (_cloudRows.Count == 0) _cloudProgressLabel.Text = "还没有歌曲。扫码获取“我喜欢的音乐”和收藏歌单，也可以打开公开歌单或搜索。";
        CloudControls();
    }
    void CloudControls()
    {
        foreach (var button in _cloudCommands) button.IsEnabled = !_cloudBusy;
        if (_cloudSync != null) _cloudSync.IsEnabled = !_cloudBusy && _cloudLocalPlaylist != null;
        if (_cloudCancel != null) _cloudCancel.IsEnabled = _cloudBusy;
        if (_cloudPlaylistList != null) _cloudPlaylistList.IsEnabled = !_cloudBusy;
        if (_cloudSourcePicker != null) _cloudSourcePicker.IsEnabled = !_cloudBusy;
        if (_cloudSearchBox != null) _cloudSearchBox.IsEnabled = !_cloudBusy;
        if (_cloudUserBox != null) _cloudUserBox.IsEnabled = !_cloudBusy;
        if (_cloudProgressBar != null) _cloudProgressBar.IsIndeterminate = _cloudBusy;
        if (_cloudProgressBar != null) _cloudProgressBar.Visibility = _cloudBusy ? Visibility.Visible : Visibility.Collapsed;
        if (_cloudEmpty != null) _cloudEmpty.Visibility = _cloudRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_cloudAccountLabel != null) _cloudAccountLabel.Text = _cloudAccount != null ? "已登录 · " + _cloudAccount.Name : _cloudPublicUser != null ? "公开账号 · " + _cloudPublicUser.Name : "支持 ID 导入 · 无需登录";
    }
    void CloudMessage(string message) { _cloudMessage = message; if (_cloudProgressLabel != null) _cloudProgressLabel.Text = message; _status.Text = message; }
    async Task CloudRun(Func<CancellationToken, Task> operation)
    {
        if (_cloudBusy) { Notify("已有在线音乐任务正在执行，可先取消。 "); return; }
        _cloudBusy = true; _cloudOperation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); CloudControls();
        try { await operation(_cloudOperation.Token); }
        catch (OperationCanceledException) { CloudMessage("已取消，已完成的下载与本地歌单已保留。 "); }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException) { CloudMessage(ex.Message); Notify(ex.Message); }
        finally { _cloudOperation.Dispose(); _cloudOperation = null; _cloudBusy = false; CloudControls(); }
    }
    async Task LoginCloud(CancellationToken token)
    {
        var window = StyledWindow("网易云音乐扫码登录", 530, 535);
        var image = new Image { Width = 220, Height = 220, Stretch = Stretch.Uniform, Margin = new(0, 10, 0, 10) };
        var status = T("正在生成二维码…", 13); status.HorizontalAlignment = HorizontalAlignment.Center;
        var explanation = T("使用网易云音乐 App 扫码并在手机上确认。登录凭据仅保留在本次运行的内存中，并发送给 api.leidell.cn。", 12, true);
        CancellationTokenSource? polling = null;
        async Task Generate()
        {
            polling?.Cancel(); var current = CancellationTokenSource.CreateLinkedTokenSource(token); polling = current;
            try
            {
                status.Text = "正在生成二维码…"; image.Source = null;
                var qr = await _cloudApi.CreateQrAsync(current.Token);
                var bytes = Convert.FromBase64String(qr.Image[(qr.Image.IndexOf(',') + 1)..]); using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); image.Source = bitmap;
                for (var i = 0; i < 60; i++)
                {
                    current.Token.ThrowIfCancellationRequested(); var result = await _cloudApi.CheckQrAsync(qr.Key, current.Token);
                    if (polling != current) return;
                    status.Text = result.Code switch { 800 => "二维码已过期，请重新生成。", 801 => "等待扫码", 802 => "已扫码，请在手机上确认登录。", 803 => "正在读取账号…", _ => "等待服务返回登录状态…" };
                    if (result.Code == 800) return;
                    if (result.Code == 803)
                    {
                        if (string.IsNullOrEmpty(result.Cookie)) throw new IOException("登录成功但服务未返回凭据，请重新扫码。");
                        _cloudApi.SetSession(result.Cookie); _cloudAccount = await _cloudApi.AccountAsync(current.Token);
                        if (_cloudAccount == null) { _cloudApi.Logout(); throw new IOException("账号会话不可用，请重新扫码。"); }
                        window.DialogResult = true; return;
                    }
                    await Task.Delay(3000, current.Token);
                }
                status.Text = "二维码已过期，请重新生成。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or FormatException or System.Net.Http.HttpRequestException) { if (polling == current) status.Text = ex.Message; }
            finally { if (polling == current) polling = null; current.Dispose(); }
        }
        window.Loaded += async (_, _) => await Generate();
        window.Closed += (_, _) => polling?.Cancel();
        var refresh = Action("重新生成", Generate, "refresh"); var cancel = Action("取消", () => { window.DialogResult = false; return Task.CompletedTask; });
        window.Content = DialogFrame(window, "连接你的网易云音乐", Stack(image, status, explanation), cancel, refresh);
        var loggedIn = window.ShowDialog() == true; polling = null;
        token.ThrowIfCancellationRequested(); if (loggedIn) await RefreshCloudAccount(token);
    }
    async Task CookieCloud(CancellationToken token)
    {
        var password = new PasswordBox { MaxLength = 32000, Padding = new(10), Margin = new(0, 12, 0, 12) };
        if (!ShowDialog("使用已有 Cookie 登录", Stack(T("凭据仅保留在本次运行的内存中，发送给 api.leidell.cn；不会发送给 GD 或音频 CDN。", 13, true), password), true, "连接账号")) return;
        _cloudApi.SetSession(password.Password.Trim()); password.Clear();
        _cloudAccount = await _cloudApi.AccountAsync(token);
        if (_cloudAccount == null) { _cloudApi.Logout(); throw new IOException("Cookie 已失效，请扫码登录。"); }
        await RefreshCloudAccount(token);
    }
    Task LogoutCloud()
    {
        if (_cloudBusy) return Task.CompletedTask;
        _cloudApi.Logout(); _cloudAccount = null; _cloudPublicUser = null; _cloudPlaylists = []; _cloudRows.Clear(); _cloudCollectionId = ""; _cloudLocalPlaylist = null;
        CloudMessage("已退出账号。电脑资料库中已下载的歌曲仍然保留。 "); CloudMusic(); return Task.CompletedTask;
    }
    async Task RefreshCloudAccount(CancellationToken token)
    {
        if (_cloudAccount == null) return;
        CloudMessage("正在加载收藏歌单…");
        var lists = await _cloudApi.PlaylistsAsync(_cloudAccount.Id, token);
        _cloudPublicUser = null;
        _cloudPlaylists = lists.Any(x => x.IsLiked) ? lists.OrderByDescending(x => x.IsLiked).ToArray() : new[] { new CloudPlaylist("likes:" + _cloudAccount.Id, "我喜欢的音乐", 0) }.Concat(lists).ToArray();
        if (_cloudPlaylistList != null) _cloudPlaylistList.ItemsSource = _cloudPlaylists;
        if (_cloudPlaylistList != null) _cloudPlaylistList.SelectedItem = _cloudPlaylists[0];
        await LoadCloudPlaylist(_cloudPlaylists[0], token);
    }
    async Task PublicCloudUser(CancellationToken token)
    {
        var input = Prompt("输入网易云用户 ID 或个人主页链接", _cloudPublicUser?.Id ?? ""); if (string.IsNullOrWhiteSpace(input)) return;
        await LookupCloudUser(input, token);
    }
    async Task LookupCloudUser(string input, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input)) { CloudMessage("请输入你的网易云用户 ID 或个人主页链接。"); return; }
        var id = CloudMusicClient.ParseId(input); CloudMessage("正在查找网易云账号…");
        var profile = await _cloudApi.UserAsync(id, token);
        var lists = await _cloudApi.PlaylistsAsync(id, token);
        _cloudPublicUser = profile; _cloudPlaylists = lists.OrderByDescending(x => x.IsLiked).ToArray();
        if (_cloudPlaylistList != null) _cloudPlaylistList.ItemsSource = _cloudPlaylists;
        CloudControls();
        if (_cloudPlaylists.Count == 0)
        { _cloudRows.Clear(); _cloudCollectionId = ""; _cloudLocalPlaylist = null; CloudControls(); CloudMessage("已找到 " + profile.Name + "，但没有可读取的公开歌单。私密收藏请扫码登录。"); return; }
        if (_cloudPlaylistList != null) _cloudPlaylistList.SelectedItem = _cloudPlaylists[0];
        await LoadCloudPlaylist(_cloudPlaylists[0], token);
        CloudMessage("已找到 " + profile.Name + " · " + _cloudPlaylists.Count + " 个歌单。" + _cloudCollectionSummary);
    }
    async Task OpenCloudPlaylist(CancellationToken token)
    {
        var input = Prompt("输入网易云歌单 ID 或歌单链接"); if (string.IsNullOrWhiteSpace(input)) return;
        await LoadCloudPlaylist(new(CloudMusicClient.ParseId(input), "", 0), token);
    }
    async Task LoadCloudPlaylist(CloudPlaylist list, CancellationToken token)
    {
        CloudMessage("正在加载全部歌曲详情…");
        if (list.Id.StartsWith("likes:", StringComparison.Ordinal))
            await SetCloudSongs(await _cloudApi.LikesAsync(list.Id[6..], token), list.Id, "网易云 · 我喜欢的音乐", token);
        else
        {
            var result = await _cloudApi.PlaylistAsync(list.Id, token);
            await SetCloudSongs(result.Songs, "playlist:" + result.Playlist.Id, "网易云 · " + result.Playlist.Name, token);
            var missingDetails = result.Songs.Count(x => !x.DetailsAvailable);
            _cloudCollectionSummary = $"歌单标记 {result.Playlist.Count} 首，返回 {result.Songs.Count} 个曲目" + (missingDetails > 0 ? $"（{missingDetails} 首详情不可用）" : "") + "。";
            if (result.Playlist.Count != result.Songs.Count || missingDetails > 0) _cloudCollectionSummary += "不可用条目会明确跳过；私密或受限内容可扫码登录后重试。";
            CloudMessage(_cloudCollectionSummary);
        }
    }
    async Task SearchCloud(int page, CancellationToken token)
    {
        var query = _cloudSearchBox?.Text.Trim() ?? _cloudQuery; if (query.Length == 0) { CloudMessage("先输入歌曲或艺术家名称。 "); return; }
        if (query != _cloudQuery) page = 1;
        CloudMessage("正在搜索…"); var songs = await _cloudApi.SearchAsync(query, _cloudSource, page, token);
        _cloudQuery = query; _cloudSearchPage = page;
        await SetCloudSongs(songs, "search:" + _cloudSource + ":" + query + ":" + page, "网易云搜索 · " + query, token);
        CloudMessage($"搜索第 {page} 页 · {songs.Count} 首，选择歌曲后下载。 ");
    }
    async Task SetCloudSongs(IReadOnlyList<CloudSong> songs, string id, string name, CancellationToken token)
    {
        var state = await Task.Run(() => { token.ThrowIfCancellationRequested(); var tracks = _store.LoadTracks().ToDictionary(x => x.Id); return songs.Select(s => new CloudRow(s) { State = tracks.TryGetValue(_store.GetPreference(CloudMusicImporter.MappingKey(s.Id)), out var t) && File.Exists(t.Path) ? "已在资料库" : s.DetailsAvailable ? "待下载" : "详情不可用" }).ToArray(); }, token);
        if (_cloudTable != null) _cloudTable.ItemsSource = null;
        _cloudRows.Clear(); foreach (var row in state) _cloudRows.Add(row);
        if (_cloudTable != null) _cloudTable.ItemsSource = _cloudRows;
        _cloudCollectionId = id; _cloudCollectionName = name; _cloudLocalPlaylist = _store.LoadPlaylists().FirstOrDefault(x => x.Id == CloudMusicImporter.PlaylistKey(id));
        _cloudCollectionSummary = $"已加载 {songs.Count} 个曲目。";
        if (_route == "网易云音乐") _subtitle.Text = name + " · " + songs.Count + " 首";
        CloudMessage(songs.Count == 0 ? "这个列表没有可读取的歌曲。" : $"已加载 {songs.Count} 首歌曲，选择下载来源后下载到电脑。 ");
        CloudControls();
    }
    async Task DownloadCloud(bool all)
    {
        if (_cloudBusy) return;
        var selected = (all ? _cloudRows : _cloudTable?.SelectedItems.Cast<CloudRow>() ?? []).Select(x => x.Song.Id).ToHashSet();
        if (selected.Count == 0) { Notify("请先选择要下载的歌曲。 "); return; }
        var collection = _cloudRows.Select(x => x.Song).ToArray(); var collectionId = _cloudCollectionId; var name = _cloudCollectionName; var source = _cloudSource;
        await CloudRun(async token =>
        {
            var activity = StartActivity("网易云下载", name + " · " + selected.Count + " 首");
            var rowMap = _cloudRows.GroupBy(x => x.Song.Id).ToDictionary(g => g.Key, g => g.First());
            var progress = new Progress<CloudTransferProgress>(p => { if (rowMap.TryGetValue(p.Id, out var row)) { row.State = p.State; var size = p.Bytes > 0 ? " · " + Bytes(p.Bytes) + (p.Total > 0 ? " / " + Bytes(p.Total.Value) : "") : ""; CloudMessage(row.Title + " · " + p.State + size); activity.Detail = row.Title + " · " + p.State; } });
            try
            {
                using var importer = new CloudMusicImporter(_store, _cloudApi);
                var result = await Task.Run(() => importer.ImportAsync(collection, selected, collectionId, name, source, progress, token), token);
                _cloudLocalPlaylist = result.Playlist; activity.State = result.Errors.Count == 0 ? "已完成" : "部分完成";
                activity.Detail = $"已导入 {result.Imported} 首，复用 {result.Reused} 首，未完成 {result.Errors.Count} 首。";
                CloudMessage(activity.Detail); Notify(activity.Detail);
                if (result.Errors.Count > 0) ShowDialog("部分歌曲未能下载", new ScrollViewer { Content = T(string.Join("\n\n", result.Errors.Take(50)), 13, true) });
            }
            catch (OperationCanceledException) { activity.State = "已取消"; foreach (var row in rowMap.Values.Where(x => x.State is "下载中" or "检查本地文件")) row.State = "已取消 · 可重试"; throw; }
            catch { activity.State = "失败"; throw; }
            finally { _tracks = await Task.Run(_store.LoadTracks); _cloudLocalPlaylist = _store.LoadPlaylists().FirstOrDefault(x => x.Id == CloudMusicImporter.PlaylistKey(collectionId)); SaveActivities(); }
        });
    }
}
