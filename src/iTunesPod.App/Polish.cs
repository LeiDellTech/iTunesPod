using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using iTunesPod.Core;

namespace iTunesPod.App;

public sealed partial class MainWindow
{
    static readonly string[] DeviceKeys =
    [
        "touch-1","touch-2","touch-3","touch-4","touch-5","touch-6","touch-7",
        "nano-1","nano-2","nano-3","nano-4","nano-5","nano-6","nano-7",
        "shuffle-1","shuffle-2","shuffle-3","shuffle-4","mini-1","mini-2",
        "classic-1","classic-2","classic-3","classic-4","classic-4-color","classic-5","classic-6","classic-7"
    ];

    static BitmapImage AssetBitmap(string relative)
    {
        var image=new BitmapImage();
        image.BeginInit();
        image.CacheOption=BitmapCacheOption.OnLoad;
        image.UriSource=new Uri("pack://application:,,,/iTunesPod.App;component/Assets/"+relative.Replace('\\','/'),UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    FrameworkElement BrandLockup()
    {
        return new Image{Source=AssetBitmap("Mix@1.5x.png"),Width=194,Height=49,Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Left};
    }

    StackPanel SidebarFooter()
    {
        var panel=Stack(Action("传输任务",()=>Go("传输任务"),"transfer"),Action("设置",()=>Go("设置"),"settings"),Action("关于 iTunesPod",()=>Go("关于"),"info"));
        panel.Children.Add(LinkLine("开发者 LeiDell","https://leidell.cn"));
        panel.Children.Add(LinkLine("GitHub 主页","https://github.com/LeiDell"));
        panel.Children.Add(LinkLine("由 WaveYY 波浪音乐支持","https://waveyy.cn"));
        var copyright=T("© WatchGeek表极客 版权所有",10,true);copyright.Margin=new(10,5,0,0);panel.Children.Add(copyright);
        return panel;
    }

    Button LinkLine(string label,string url)
    {
        var button=Action(label,()=>{OpenLink(url);return Task.CompletedTask;});
        button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Padding=new Thickness(10,4,10,4);button.FontSize=10;
        if(button.Content is TextBlock text){text.FontSize=10;text.Foreground=B("Muted");}
        return button;
    }

    static void OpenLink(string url)=>Process.Start(new ProcessStartInfo(url){UseShellExecute=true});

    FrameworkElement LoadingView()
    {
        var progress=new ProgressBar{IsIndeterminate=true,Width=220,Height=4,Foreground=B("Accent"),Background=B("Panel"),BorderThickness=new(0)};
        var content=Stack(new Image{Source=AssetBitmap("Logo@1.5x.png"),Width=88,Height=88,Stretch=Stretch.Uniform},T("正在准备你的音乐资料库",23),T("预载封面和设备素材，稍后浏览会更流畅。",12,true),progress);
        foreach(var child in content.Children.OfType<FrameworkElement>()){child.HorizontalAlignment=HorizontalAlignment.Center;child.Margin=new(0,7,0,7);}return new Grid{Children={content}};
    }

    async Task PreloadVisualsAsync()
    {
        _status.Text="正在预载封面与设备素材…";
        foreach(var key in DeviceKeys) _=AssetBitmap("Devices/"+key+".png");
        var paths=_tracks.Select(x=>x.CoverPath).Where(x=>x!=null&&File.Exists(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).Cast<string>().ToArray();
        var decoded=await Task.Run(()=>paths.Select(path=>{try{using var stream=File.OpenRead(path);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=600;image.StreamSource=stream;image.EndInit();image.Freeze();return(path,image:(BitmapSource?)image);}catch{return(path,image:(BitmapSource?)null);}}).Where(x=>x.image!=null).ToArray());
        foreach(var (path,image) in decoded)_images[path]=image!;
        _status.Text=$"已预载 {decoded.Length} 张封面";
    }

    void AnimatePage()
    {
        if(_page.Content is not FrameworkElement element)return;
        element.Opacity=0;var transform=new TranslateTransform(0,10);element.RenderTransform=transform;
        var ease=new CubicEase{EasingMode=EasingMode.EaseOut};
        var fade=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180)){EasingFunction=ease};
        var rise=new DoubleAnimation(10,0,TimeSpan.FromMilliseconds(210)){EasingFunction=ease};
        Timeline.SetDesiredFrameRate(fade,60);Timeline.SetDesiredFrameRate(rise,60);
        element.BeginAnimation(OpacityProperty,fade,HandoffBehavior.SnapshotAndReplace);transform.BeginAnimation(TranslateTransform.YProperty,rise,HandoffBehavior.SnapshotAndReplace);
    }

    static void PolishButton(Button button)
    {
        button.RenderTransformOrigin=new Point(.5,.5);var scale=new ScaleTransform(1,1);button.RenderTransform=scale;
        button.MouseEnter+=(_,_)=>AnimateScale(scale,1.018);button.MouseLeave+=(_,_)=>AnimateScale(scale,1);button.PreviewMouseLeftButtonDown+=(_,_)=>AnimateScale(scale,.97);button.PreviewMouseLeftButtonUp+=(_,_)=>AnimateScale(scale,1.018);
    }
    static void AnimateScale(ScaleTransform scale,double to){var animation=new DoubleAnimation(to,TimeSpan.FromMilliseconds(105)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};Timeline.SetDesiredFrameRate(animation,60);scale.BeginAnimation(ScaleTransform.ScaleXProperty,animation);scale.BeginAnimation(ScaleTransform.ScaleYProperty,animation);}

    FrameworkElement DeviceProductImage(DeviceSession device,double width=190,double height=310)
    {
        var family=device.Identity.Family.ToLowerInvariant();var generation=device.Identity.Generation;
        var number=new string(generation.Where(char.IsDigit).ToArray());if(number.Length==0)number="1";
        var stem=family.Contains("nano")?"nano":family.Contains("shuffle")?"shuffle":family.Contains("mini")?"mini":family.Contains("touch")?"touch":"classic";
        var key=$"{stem}-{number}";if(!DeviceKeys.Contains(key))key=stem switch{"nano"=>"nano-4","shuffle"=>"shuffle-4","mini"=>"mini-2","touch"=>"touch-7",_=>"classic-7"};
        BitmapSource source=AssetBitmap("Devices/"+key+".png");
        if(key=="nano-4"){
            var color=device.Identity.Color.ToLowerInvariant();var index=color.Contains("orange")?1:color.Contains("yellow")?2:color.Contains("green")?3:color.Contains("blue")?4:color.Contains("pink")?5:color.Contains("purple")?6:color.Contains("silver")?7:color.Contains("black")?8:0;
            var segment=source.PixelWidth/9;source=new CroppedBitmap(source,new Int32Rect(index*segment,0,index==8?source.PixelWidth-index*segment:segment,source.PixelHeight));source.Freeze();
        }
        return new Image{Source=source,Width=width,Height=height,Stretch=Stretch.Uniform,SnapsToDevicePixels=true};
    }

    void About()
    {
        _title.Text="关于 iTunesPod";_subtitle.Text="作品信息、设备图鉴与开源声明";
        var panel=Stack();panel.MaxWidth=1040;panel.HorizontalAlignment=HorizontalAlignment.Left;
        var hero=Grid("130,*","*");hero.Children.Add(new Image{Source=AssetBitmap("Logo@1.5x.png"),Width=104,Height=104,Stretch=Stretch.Uniform});
        var intro=Stack(T("iTunesPod",34),T("0.4.0 · Windows / .NET 8 / C# / WPF",13,true),T("为经典 iPod 设计的本地音乐、照片、播客与设备同步工具。",15));Col(intro,1);hero.Children.Add(intro);panel.Children.Add(hero);
        panel.Children.Add(T("制作与支持",23));panel.Children.Add(Row(Action("开发者 LeiDell",()=>{OpenLink("https://leidell.cn");return Task.CompletedTask;},null,true),Action("GitHub 主页",()=>{OpenLink("https://github.com/LeiDell");return Task.CompletedTask;}),Action("WaveYY 波浪音乐",()=>{OpenLink("https://waveyy.cn");return Task.CompletedTask;})));panel.Children.Add(T("© WatchGeek表极客 版权所有",13,true));
        panel.Children.Add(T("开源声明",23));panel.Children.Add(T("本项目使用 .NET 8 / WPF、NAudio、TagLibSharp、Microsoft.Data.Sqlite、SQLitePCLRaw、xUnit、coverlet 与 FFmpeg；设备目录和部分协议资料参考 iOpenPod。MiSans 依照小米字体许可在系统安装后使用，字体文件不随应用再次分发。完整版本、许可证与源码链接均收录在 THIRD-PARTY-NOTICES.md。",13,true));panel.Children.Add(Row(Action("查看完整第三方许可",()=>{var path=Path.Combine(AppContext.BaseDirectory,"THIRD-PARTY-NOTICES.md");if(File.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});return Task.CompletedTask;}),Action("Apple iPod 型号资料",()=>{OpenLink("https://support.apple.com/zh-cn/103823");return Task.CompletedTask;}),Action("MacDB 型号资料",()=>{OpenLink("https://macdb.cn/");return Task.CompletedTask;})));
        panel.Children.Add(T("iPod 型号图鉴",23));panel.Children.Add(T("覆盖 iPod touch、nano、shuffle、mini 与 classic 各世代；图片已去除网页背景，便于在深浅主题中统一呈现。",12,true));
        var gallery=new WrapPanel();foreach(var key in DeviceKeys){var name=key.Replace("touch-","iPod touch 第 ").Replace("nano-","iPod nano 第 ").Replace("shuffle-","iPod shuffle 第 ").Replace("mini-","iPod mini 第 ").Replace("classic-","iPod classic 第 ");if(key=="classic-4-color")name="iPod classic 第 4 代彩屏";else name+=" 代";var card=Stack(new Image{Source=AssetBitmap("Devices/"+key+".png"),Width=128,Height=138,Stretch=Stretch.Uniform},T(name,11));card.Width=162;card.Margin=new(0,12,16,6);card.HorizontalAlignment=HorizontalAlignment.Center;gallery.Children.Add(new Border{Child=card,Padding=new(10),CornerRadius=new(12),Background=B("Panel")});}panel.Children.Add(gallery);
        foreach(var child in panel.Children.OfType<FrameworkElement>())child.Margin=new(0,0,0,18);_page.Content=new ScrollViewer{Content=panel};
    }

    Window StyledWindow(string title,double width=620,double height=520)
    {
        var window=new Window{Owner=this,Title=title,Width=width,Height=height,MinWidth=440,MinHeight=320,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=B("Canvas"),Foreground=B("Ink"),Resources=Resources,FontFamily=new FontFamily("MiSans"),FontWeight=FontWeights.SemiBold,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.CanResizeWithGrip,ShowInTaskbar=false,Icon=AssetBitmap("Logo@1.5x.png")};
        return window;
    }

    Grid DialogFrame(Window window,string title,UIElement body,params Button[] actions)
    {
        var root=Grid("*","56,*,Auto");var header=Grid("*,Auto","*");header.Background=B("Panel");header.MouseLeftButtonDown+=(_,e)=>{if(e.ButtonState==MouseButtonState.Pressed)window.DragMove();};var label=T(title,17);label.Margin=new(20,0,0,0);header.Children.Add(label);var close=Action("",()=>{window.DialogResult=false;return Task.CompletedTask;});close.Content=Glyph("close",14,B("Muted"));close.Padding=new(18);Col(close,1);header.Children.Add(close);root.Children.Add(header);
        var holder=new Border{Child=body,Padding=new(24)};At(holder,1);root.Children.Add(holder);var bar=Row(actions);bar.HorizontalAlignment=HorizontalAlignment.Right;bar.Margin=new(24,0,24,20);At(bar,2);root.Children.Add(bar);return root;
    }

    static class MessageBox
    {
        public static MessageBoxResult Show(MainWindow owner,string message,string title,MessageBoxButton buttons=MessageBoxButton.OK)
        {
            var window=owner.StyledWindow(title,500,310);MessageBoxResult result=MessageBoxResult.Cancel;
            var ok=owner.Action(buttons==MessageBoxButton.OK?"知道了":"确认",()=>{result=MessageBoxResult.OK;window.DialogResult=true;return Task.CompletedTask;},null,true);
            Button[] actions=buttons==MessageBoxButton.OKCancel?[owner.Action("取消",()=>{result=MessageBoxResult.Cancel;window.DialogResult=false;return Task.CompletedTask;}),ok]:[ok];
            window.Content=owner.DialogFrame(window,title,Stack(T(message,14,true)),actions);window.ShowDialog();return result;
        }
    }

    public static void ShowUnhandled(string message)
    {
        if(Application.Current?.MainWindow is MainWindow owner)MessageBox.Show(owner,message,"iTunesPod");
    }

    abstract class UnifiedPicker
    {
        protected readonly bool Folders;protected readonly bool Save;public string Title{get;set;}="选择文件";public string Filter{get;set;}="所有文件|*.*";public bool Multiselect{get;set;}public string FileName{get;set;}="";public string[] FileNames{get;protected set;}=[];public string FolderName{get;protected set;}="";public string[] FolderNames{get;protected set;}=[];
        protected UnifiedPicker(bool folders,bool save=false){Folders=folders;Save=save;}
        public bool? ShowDialog(MainWindow owner)
        {
            var window=owner.StyledWindow(Title,760,580);var current=Directory.Exists(Path.GetDirectoryName(FileName))?Path.GetDirectoryName(FileName)!:Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);var pathBox=new TextBox{Text=current};var list=new ListBox{SelectionMode=Multiselect?SelectionMode.Extended:SelectionMode.Single,BorderThickness=new(0),Background=owner.B("Canvas")};var name=new TextBox{Text=Path.GetFileName(FileName),Visibility=Save?Visibility.Visible:Visibility.Collapsed};
            string[] patterns=Filter.Split('|').Where((_,i)=>i%2==1).SelectMany(x=>x.Split(';')).Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();if(patterns.Length==0)patterns=["*.*"];
            void Load(string folder){try{folder=Path.GetFullPath(folder);var items=new List<PickerEntry>();if(Directory.GetParent(folder)!=null)items.Add(new("..",Directory.GetParent(folder)!.FullName,true));items.AddRange(Directory.EnumerateDirectories(folder).OrderBy(x=>x).Select(x=>new PickerEntry("📁  "+Path.GetFileName(x),x,true)));if(!Folders)items.AddRange(patterns.SelectMany(p=>Directory.EnumerateFiles(folder,p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).Select(x=>new PickerEntry(Path.GetFileName(x),x,false)));list.ItemsSource=items;pathBox.Text=folder;current=folder;}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){owner.Notify("无法打开目录："+ex.Message);}}
            list.MouseDoubleClick+=(_,_)=>{if(list.SelectedItem is PickerEntry item){if(item.Directory)Load(item.Path);else if(!Save){FileName=item.Path;FileNames=[item.Path];window.DialogResult=true;}}};pathBox.KeyDown+=(_,e)=>{if(e.Key==Key.Enter)Load(pathBox.Text);};
            var browse=Grid("Auto,*","Auto,*");var drives=new ComboBox{ItemsSource=DriveInfo.GetDrives().Where(x=>x.IsReady).Select(x=>x.RootDirectory.FullName).ToArray(),MinWidth=90};drives.SelectionChanged+=(_,_)=>{if(drives.SelectedItem is string root)Load(root);};browse.Children.Add(drives);Col(pathBox,1);browse.Children.Add(pathBox);At(list,1);System.Windows.Controls.Grid.SetColumnSpan(list,2);browse.Children.Add(list);
            var body=Grid("*","*,Auto");body.Children.Add(browse);At(name,1);name.Margin=new(0,12,0,0);body.Children.Add(name);
            var cancel=owner.Action("取消",()=>{window.DialogResult=false;return Task.CompletedTask;});var choose=owner.Action(Save?"保存":Folders?"选择文件夹":"打开",()=>{var selected=list.SelectedItems.Cast<PickerEntry>().ToArray();if(Save){if(string.IsNullOrWhiteSpace(name.Text)){owner.Notify("请输入文件名。");return Task.CompletedTask;}FileName=Path.Combine(current,name.Text.Trim());FileNames=[FileName];}else if(Folders){var dirs=selected.Where(x=>x.Directory&&x.Name!="..").Select(x=>x.Path).ToArray();if(dirs.Length==0)dirs=[current];FolderNames=dirs;FolderName=dirs[0];}else{var files=selected.Where(x=>!x.Directory).Select(x=>x.Path).ToArray();if(files.Length==0){owner.Notify("请选择文件。");return Task.CompletedTask;}FileNames=Multiselect?files:[files[0]];FileName=FileNames[0];}window.DialogResult=true;return Task.CompletedTask;},null,true);
            window.Content=owner.DialogFrame(window,Title,body,cancel,choose);Load(current);return window.ShowDialog();
        }
        sealed record PickerEntry(string Name,string Path,bool Directory){public override string ToString()=>Name;}
    }
    sealed class OpenFileDialog:UnifiedPicker{public OpenFileDialog():base(false){}}
    sealed class SaveFileDialog:UnifiedPicker{public SaveFileDialog():base(false,true){}}
    sealed class OpenFolderDialog:UnifiedPicker{public OpenFolderDialog():base(true){}}
}
