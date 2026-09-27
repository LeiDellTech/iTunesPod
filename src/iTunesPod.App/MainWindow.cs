using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using iTunesPod.Core;
using iTunesPod.Infrastructure;
using Microsoft.Win32;
namespace iTunesPod.App;
public sealed partial class MainWindow : Window
{
    readonly Dictionary<string,ThemeColor> _colors=[];
    readonly LibraryStore _store;
    readonly DeviceAccess _devices=new();
    readonly PlaybackController _player=new();
    readonly Dictionary<string,DeviceSession> _sessions=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,string> _stamps=new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _draft=[];
    readonly Stack<string[]> _undo=new();
    readonly Dictionary<string,Button> _nav=[];
    readonly StackPanel _sidebar=new(),_deviceNav=new(),_tabs=new(){Orientation=Orientation.Horizontal};
    readonly TextBox _search=new(){ToolTip="搜索歌曲、艺术家、专辑"};
    readonly TextBlock _title=T("新发现",34),_subtitle=T("",12,true),_status=T("准备就绪",11,true),_message=T("",12);
    readonly ContentControl _page=new(),_details=new(){Visibility=Visibility.Collapsed},_nowArt=new();
    readonly Grid _body=new(),_content=new();
    readonly TextBlock _nowTitle=T("iTunesPod",15),_nowArtist=T("选择音乐开始播放",11,true),_elapsed=T("0:00",10,true),_duration=T("0:00",10,true);
    readonly Slider _seek=new(){Minimum=0,Maximum=1},_volume=new(){Minimum=0,Maximum=1,Value=.5,Width=75};
    readonly Button _play=new();
    readonly Border _banner=new(){Visibility=Visibility.Collapsed},_draftBar=new(){Visibility=Visibility.Collapsed};
    readonly DispatcherTimer _progress=new(){Interval=TimeSpan.FromMilliseconds(350)},_detect=new(){Interval=TimeSpan.FromSeconds(4)};
    readonly CancellationTokenSource _lifetime=new();
    readonly Dictionary<string,BitmapSource> _images=new(StringComparer.OrdinalIgnoreCase);
    readonly List<Activity> _activities=[];
    DeviceNotifications? _notifications;
    CancellationTokenSource? _scan;
    DeviceSession? _device;
    readonly AsyncLocal<DeviceSession?> _operationDevice=new();
    DeviceSession? TargetDevice=>_operationDevice.Value??_device;
    IReadOnlyList<Track> _tracks=[],_queue=[];
    DataGrid? _table;
    Action<string>? _filter;
    string _route="新发现",_deviceTab="概览";
    bool _discovering,_seeking,_closed,_dark,_routingSearch;
    readonly string[] _args;
    public MainWindow(string[] args){
        _args=args;Title="iTunesPod";Width=1440;Height=960;MinWidth=1060;MinHeight=720;FontFamily=new("MiSans");FontWeight=FontWeights.SemiBold;FontSize=14;Icon=AssetBitmap("Logo@1.5x.png");
        Resources=(ResourceDictionary)Application.LoadComponent(new Uri("/iTunesPod.App;component/Theme.xaml",UriKind.Relative));
        foreach(var key in new[]{"Canvas","Panel","Ink","Muted","Line","Hover","Selection","Accent"}){var state=new ThemeColor{Value=((SolidColorBrush)Resources[key]).Color};_colors[key]=state;var brush=new SolidColorBrush();System.Windows.Data.BindingOperations.SetBinding(brush,SolidColorBrush.ColorProperty,new System.Windows.Data.Binding("Value"){Source=state});Resources[key]=brush;}
        var index=Array.IndexOf(args,"--live-preview");var smoke=Array.IndexOf(args,"--smoke-test");var output=index>=0&&args.Length>index+2?args[index+2]:smoke>=0&&args.Length>smoke+1?args[smoke+1]:null;
        _store=new(output==null?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"iTunesPod"):Path.Combine(output,"test-library"));
        UiLanguage.Traditional=_store.GetPreference("language","simplified")=="traditional";_dark=_store.GetPreference("theme","light")=="dark";ApplyTheme();BuildShell();
        try{_activities.AddRange(JsonSerializer.Deserialize<List<Activity>>(_store.GetPreference("activities","[]"))??[]);}catch(JsonException){}
        AllowDrop=true;Drop+=async(_,e)=>{if(e.Data.GetData(DataFormats.FileDrop) is string[] files)await Guard(()=>Import(files));};
        _search.TextChanged+=(_,_)=>{if(_routingSearch)return;var q=_search.Text.Trim();if(_filter!=null){_filter(q);return;}if(q.Length==0)return;_routingSearch=true;if(_tracks.Count>0||_device==null)Navigate("歌曲");else{_deviceTab="歌曲";Navigate("设备");}_search.Text=q;_search.CaretIndex=q.Length;_routingSearch=false;_filter?.Invoke(q);};
        PreviewKeyDown+=(_,e)=>{if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)&&e.Key==Key.F){_search.Focus();_search.SelectAll();e.Handled=true;}if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)&&e.Key==Key.Z){UndoDraft();e.Handled=true;}};
        _play.Click+=async(_,_)=>await Guard(TogglePlayback);_volume.ValueChanged+=(_,_)=>_player.Volume=(float)_volume.Value;
        _seek.PreviewMouseLeftButtonDown+=(_,_)=>_seeking=true;_seek.PreviewMouseLeftButtonUp+=(_,_)=>{_player.Seek(_seek.Value);_seeking=false;};
        _player.Changed+=()=>Dispatcher.BeginInvoke(UpdatePlayer);_player.Ended+=track=>Dispatcher.BeginInvoke(async()=>{if(_closed)return;if(!track.Id.StartsWith("device:")){var current=_tracks.FirstOrDefault(x=>x.Id==track.Id);if(current!=null){_store.SaveTracks([current with{PlayCount=checked(current.PlayCount+1),LastPlayedUtc=DateTimeOffset.UtcNow,BookmarkMilliseconds=0}]);_tracks=_store.LoadTracks();}}if(track.IsPodcast&&track.EpisodeId.Length>0)MarkEpisodePlayed(track.EpisodeId,true);await Guard(()=>Step(1));});_progress.Tick+=(_,_)=>{UpdateProgress();SavePositionPeriodically();};_detect.Tick+=async(_,_)=>await Guard(()=>Discover(false));
        Loaded+=async(_,_)=>{await Guard(async()=>{_page.Content=LoadingView();_tracks=await Task.Run(_store.LoadTracks);await PreloadVisualsAsync();Navigate(_args.Contains("--online-music")?"网易云音乐":"新发现");if(output!=null){await Validate(output,index>=0?args[index+1]:null);Close();return;}_notifications=new();_notifications.Changed+=()=>Dispatcher.BeginInvoke(async()=>await Guard(()=>Discover(true)));_notifications.Start();await Discover(false);_detect.Start();_progress.Start();SetupWatches();var cloudUserAt=Array.IndexOf(_args,"--cloud-user");if(cloudUserAt>=0&&_args.Length>cloudUserAt+1){Navigate("网易云音乐");await CloudRun(t=>LookupCloudUser(_args[cloudUserAt+1],t));}});};
        Closed+=(_,_)=>{SavePlaybackPosition();_closed=true;_lifetime.Cancel();_cloudOperation?.Cancel();_cloudApi.Dispose();_scan?.Cancel();_progress.Stop();_detect.Stop();_notifications?.Dispose();_watchTimer.Stop();foreach(var watcher in _watches)watcher.Dispose();_player.Dispose();};
    }
    void BuildShell(){
        var root=Grid("248,*","*");root.SetResourceReference(BackgroundProperty,"Canvas");var side=Grid("*","Auto,*,Auto");side.Margin=new(18,20,18,14);
        var logo=BrandLockup();logo.Margin=new(6,0,0,22);side.Children.Add(logo);
        var searchFrame=new Border{BorderBrush=B("Line"),BorderThickness=new(1),CornerRadius=new(5),Background=B("Canvas"),Padding=new(9,0,9,0),Margin=new(0,0,0,18)};var searchGrid=Grid("22,*","*");searchGrid.Children.Add(Glyph("search",14,B("Muted")));_search.BorderThickness=new(0);_search.Background=Brushes.Transparent;_search.MinHeight=32;Col(_search,1);searchGrid.Children.Add(_search);searchFrame.Child=searchGrid;_sidebar.Children.Add(searchFrame);
        AddNav("主页","home");AddNav("新发现","star");AddNav("最近添加","clock");Section("资料库");foreach(var (label,icon) in new[]{("艺术家","person"),("专辑","album"),("歌曲","music"),("播放列表","list"),("视频","video"),("Podcast","mic"),("图片","photo")})AddNav(label,icon);
        Section("在线音乐");AddNav("网易云音乐","music");
        Section("设备");_sidebar.Children.Add(_deviceNav);RebuildDevices();_sidebar.Children.Add(Action("打开设备目录",OpenDevice,"add"));var navScroll=new ScrollViewer{Content=_sidebar};At(navScroll,1);side.Children.Add(navScroll);
        var bottom=SidebarFooter();At(bottom,2);side.Children.Add(bottom);
        root.Children.Add(new Border{Background=B("Panel"),BorderBrush=B("Line"),BorderThickness=new(0,0,1,0),Child=side});
        var main=Grid("*","68,*,Auto,24");Col(main,1);root.Children.Add(main);main.Children.Add(BuildPlayer());
        _body.RowDefinitions.Add(new(){Height=GridLength.Auto});_body.RowDefinitions.Add(new(){Height=GridLength.Auto});_body.RowDefinitions.Add(new(){Height=GridLength.Auto});_body.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});_body.Margin=new(40,32,40,12);At(_body,1);main.Children.Add(_body);
        var heading=Stack(_title,_subtitle);heading.Margin=new(0,0,0,22);_title.FontWeight=FontWeights.Bold;_subtitle.Margin=new(0,7,0,0);_body.Children.Add(heading);
        _banner.Child=_message;_banner.Background=B("Selection");_banner.Padding=new(12);_banner.CornerRadius=new(5);_banner.Margin=new(0,0,0,16);At(_banner,1);_body.Children.Add(_banner);
        _tabs.Margin=new(0,0,0,20);At(_tabs,2);_body.Children.Add(_tabs);
        _content.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});_content.ColumnDefinitions.Add(new(){Width=new(0)});_content.Children.Add(_page);Col(_details,1);_content.Children.Add(_details);At(_content,3);_body.Children.Add(_content);
        At(_draftBar,2);main.Children.Add(_draftBar);_status.Margin=new(40,0,0,0);At(_status,3);main.Children.Add(_status);Content=root;
    }
    FrameworkElement BuildPlayer(){
        var bar=Grid("180,*,180,110","*");bar.Margin=new(30,7,25,7);
        var controls=Row(IconButton("shuffle","随机播放",Shuffle),IconButton("previous","上一首",()=>Step(-1)));System.Windows.Automation.AutomationProperties.SetName(_play,"播放 / 暂停");System.Windows.Automation.AutomationProperties.SetName(_seek,"播放位置");System.Windows.Automation.AutomationProperties.SetName(_volume,"音量");_play.Content=Glyph("play",25,B("Muted"));_play.Padding=new(10,6,10,6);controls.Children.Add(_play);controls.Children.Add(IconButton("next","下一首",()=>Step(1)));controls.VerticalAlignment=VerticalAlignment.Center;bar.Children.Add(controls);
        var now=Grid("50,*","*");now.MaxWidth=570;now.Background=B("Panel");_nowArt.Content=Cover(null,46);now.Children.Add(_nowArt);
        var song=Grid("*","Auto,Auto,Auto");song.Margin=new(12,2,12,0);var title=Row(_nowTitle,_nowArtist);title.HorizontalAlignment=HorizontalAlignment.Center;_nowTitle.FontWeight=FontWeights.SemiBold;_nowTitle.MaxWidth=230;_nowTitle.TextTrimming=TextTrimming.CharacterEllipsis;_nowArtist.MaxWidth=210;_nowArtist.TextTrimming=TextTrimming.CharacterEllipsis;song.Children.Add(title);
        _seek.Height=13;_seek.Margin=new(0,2,0,0);At(_seek,1);song.Children.Add(_seek);var times=Grid("*,*","*");times.Children.Add(_elapsed);_duration.HorizontalAlignment=HorizontalAlignment.Right;Col(_duration,1);times.Children.Add(_duration);At(times,2);song.Children.Add(times);Col(song,1);now.Children.Add(song);Col(now,1);bar.Children.Add(now);
        var right=Row(Glyph("volume",16,B("Muted")),_volume,IconButton("list","播放队列",ShowQueue));right.HorizontalAlignment=HorizontalAlignment.Right;right.VerticalAlignment=VerticalAlignment.Center;Col(right,2);bar.Children.Add(right);
        var add=Action("导入音乐",PickFiles,null,true);add.Margin=new(12,4,0,4);Col(add,3);bar.Children.Add(add);
        return new Border{Background=B("Canvas"),BorderBrush=B("Line"),BorderThickness=new(0,0,0,1),Child=bar};
    }
    void AddNav(string label,string icon){var button=Action(label,()=>Go(label),icon);button.Style=(Style)Resources["Nav"];if(button.Content is StackPanel navRow && navRow.Children[0] is TextBlock navIcon)navIcon.Foreground=B("Accent");_nav[label]=button;_sidebar.Children.Add(button);}
    void Section(string label){var text=T(label,11,true);text.Margin=new(10,22,0,7);text.FontWeight=FontWeights.SemiBold;_sidebar.Children.Add(text);}
    Task Go(string route){Navigate(route);return Task.CompletedTask;}
    void Navigate(string route){var routeChanged=!string.Equals(_route,route,StringComparison.Ordinal);_route=route;HideDetails();_table=null;_filter=null;_search.Text="";_tabs.Children.Clear();_title.Text=UiLanguage.Label(route);_subtitle.Text="";foreach(var (key,b) in _nav)b.Background=key==route?B("Hover"):Brushes.Transparent;
        switch(route){case "主页":case "新发现":Home();break;case "最近添加":case "专辑":Albums(_tracks.Where(x=>!x.IsVideo));break;case "歌曲":Music(_tracks.Where(x=>!x.IsVideo));break;case "艺术家":Artists(_tracks);break;case "播放列表":Playlists();break;case "设备":Device();break;case "设置":Settings();break;case "关于":About();break;case "传输任务":Transfers();break;case "图片":Photos();break;case "视频":Music(_tracks.Where(x=>x.IsVideo));_subtitle.Text="本地视频 · 使用系统默认播放器";break;case "Podcast":Podcasts();break;case "网易云音乐":CloudMusic();break;}_tabs.Visibility=_tabs.Children.Count>0?Visibility.Visible:Visibility.Collapsed;UpdateDraft();if(routeChanged)AnimatePage();
    }
    void Notify(string text){_message.Text=text;_banner.Visibility=Visibility.Visible;_status.Text=text;}
    async Task Guard(Func<Task> action){try{await action();}catch(OperationCanceledException){Notify("已取消。");}catch(Exception ex){Notify(ex.Message);if(_args.Contains("--live-preview")||_args.Contains("--smoke-test")){var at=Array.IndexOf(_args,"--live-preview");var folder=at>=0?_args[at+2]:_args[Array.IndexOf(_args,"--smoke-test")+1];Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,"FAILED.txt"),ex.ToString());Environment.ExitCode=1;Close();}}}
    Button Action(string label,Func<Task> action,string? icon=null,bool primary=false){var button=new Button{Content=icon==null?T(label,13):Row(Glyph(icon,16),T(label,13))};if(primary){button.Style=(Style)Resources["Primary"];if(button.Content is TextBlock text)text.Foreground=Brushes.White;else if(button.Content is StackPanel row)foreach(var child in row.Children.OfType<TextBlock>())child.Foreground=Brushes.White;}System.Windows.Automation.AutomationProperties.SetName(button,UiLanguage.Label(label));button.Click+=async(_,_)=>{button.IsEnabled=false;var previous=_operationDevice.Value;_operationDevice.Value=_device;try{await Guard(action);}finally{_operationDevice.Value=previous;button.IsEnabled=true;}};PolishButton(button);return button;}
    Button IconButton(string icon,string tip,Func<Task> action){var b=Action("",action);b.Content=Glyph(icon,17,B("Muted"));b.ToolTip=UiLanguage.Label(tip);System.Windows.Automation.AutomationProperties.SetName(b,UiLanguage.Label(tip));b.Padding=new(7);return b;}
    void ApplyTheme(){var colors=new Dictionary<string,string>{{"Canvas",_dark?"#1D1D1F":"#FFFFFF"},{"Panel",_dark?"#252526":"#F4F4F4"},{"Ink",_dark?"#F5F5F7":"#222222"},{"Muted",_dark?"#A1A1A6":"#777777"},{"Line",_dark?"#373739":"#E5E5E5"},{"Hover",_dark?"#3D3D40":"#EAEAEA"},{"Selection",_dark?"#493038":"#FCE8EC"}};if(SystemParameters.HighContrast){colors["Canvas"]=SystemColors.WindowColor.ToString();colors["Panel"]=SystemColors.ControlColor.ToString();colors["Ink"]=SystemColors.WindowTextColor.ToString();colors["Muted"]=SystemColors.WindowTextColor.ToString();colors["Line"]=SystemColors.WindowTextColor.ToString();colors["Hover"]=SystemColors.ControlColor.ToString();colors["Selection"]=SystemColors.HighlightColor.ToString();}foreach(var (key,color) in colors)_colors[key].Value=(Color)ColorConverter.ConvertFromString(color);Background=B("Canvas");Foreground=B("Ink");}
    Brush B(string key)=>(Brush)Resources[key];
    static TextBlock T(string text,double size=14,bool muted=false){var t=new TextBlock{Text=UiLanguage.Label(text),FontSize=size,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};t.SetResourceReference(TextBlock.ForegroundProperty,muted?"Muted":"Ink");return t;}
    static StackPanel Stack(params UIElement[] items){var p=new StackPanel();foreach(var item in items)p.Children.Add(item);return p;}
    static StackPanel Row(params UIElement[] items){var p=Stack(items);p.Orientation=Orientation.Horizontal;foreach(var e in items.OfType<FrameworkElement>())e.Margin=new(0,0,9,0);return p;}
    static Grid Grid(string columns,string rows){var g=new Grid();foreach(var c in columns.Split(','))g.ColumnDefinitions.Add(new(){Width=Length(c)});foreach(var r in rows.Split(','))g.RowDefinitions.Add(new(){Height=Length(r)});return g;}
    static GridLength Length(string text)=>text=="Auto"?GridLength.Auto:text=="*"?new(1,GridUnitType.Star):new(double.Parse(text,System.Globalization.CultureInfo.InvariantCulture));
    static void Col(UIElement e,int i)=>System.Windows.Controls.Grid.SetColumn(e,i);static void At(UIElement e,int i)=>System.Windows.Controls.Grid.SetRow(e,i);
    static TextBlock Glyph(string icon,double size=18,Brush? color=null){var glyph=icon switch{"music"=>"\uE8D6","home"=>"\uE80F","star"=>"\uE734","clock"=>"\uE823","person"=>"\uE77B","album"=>"\uE93C","list"=>"\uE8F1","video"=>"\uE714","photo"=>"\uE91B","mic"=>"\uE720","usb"=>"\uE88E","add"=>"\uE710","search"=>"\uE721","play"=>"\uE768","pause"=>"\uE769","previous"=>"\uE892","next"=>"\uE893","shuffle"=>"\uE8B1","volume"=>"\uE767","settings"=>"\uE713","transfer"=>"\uE895","folder"=>"\uE8B7","info"=>"\uE946","close"=>"\uE8BB","export"=>"\uE74E","refresh"=>"\uE72C","check"=>"\uE73E",_=>"\uE946"};return new(){Text=glyph,FontFamily=new("Segoe MDL2 Assets"),FontSize=size,Foreground=color??Brushes.Gray,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};}
    static string Bytes(long bytes)=>bytes>=1_000_000_000?$"{bytes/1_000_000_000d:0.00} GB":$"{bytes/1_000_000d:0.0} MB";
    static string Time(double seconds)=>TimeSpan.FromSeconds(Math.Max(0,seconds)).ToString(seconds>=3600?@"h\:mm\:ss":@"m\:ss");
    FrameworkElement Cover(Track? track,double size)=>ImageCover(track?.CoverPath,size);
    FrameworkElement ImageCover(string? path,double size){var border=new Border{Width=size,Height=size,CornerRadius=new(6),Background=B("Panel"),ClipToBounds=true};if(path!=null&&File.Exists(path)){try{if(!_images.TryGetValue(path,out var bitmap)){using var stream=File.OpenRead(path);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=600;image.StreamSource=stream;image.EndInit();image.Freeze();_images[path]=bitmap=image;}border.Background=new ImageBrush(bitmap){Stretch=Stretch.UniformToFill,AlignmentX=AlignmentX.Center,AlignmentY=AlignmentY.Center};}catch(Exception e)when(e is IOException or NotSupportedException or System.IO.FileFormatException){border.Child=Glyph("music",size*.3,B("Muted"));}}else border.Child=Glyph("music",size*.3,B("Muted"));return border;}
    void HideDetails(){_details.Content=null;_details.Visibility=Visibility.Collapsed;_content.ColumnDefinitions[1].Width=new(0);}
    FrameworkElement Empty(string title,string text,params UIElement[] actions){var content=Stack(Glyph("music",42,B("Accent")),T(title,24),T(text,14,true),Row(actions));foreach(var e in content.Children.OfType<FrameworkElement>())e.Margin=new(0,14,0,0);return new Border{Child=content,MaxWidth=600,Padding=new(32),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};}
    sealed class ThemeColor : System.ComponentModel.INotifyPropertyChanged{Color _value;public Color Value{get=>_value;set{_value=value;PropertyChanged?.Invoke(this,new("Value"));}}public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;}
    sealed class Activity{public string Title{get;set;}="";public string Detail{get;set;}="";public string State{get;set;}="进行中";public DateTime Time{get;set;}=DateTime.Now;}
}




