using System.IO;
using System.Windows;
using System.Windows.Controls;
using iTunesPod.Infrastructure;
namespace iTunesPod.App;
public sealed partial class MainWindow
{
    readonly Dictionary<string,CancellationTokenSource> _downloads=[];
    DateTime _lastPositionSave=DateTime.MinValue;
    void SavePlaybackPosition()
    {
        var playing=_player.Current;if(playing==null||!playing.IsPodcast||playing.Id.StartsWith("device:"))return;
        var track=_tracks.FirstOrDefault(x=>x.Id==playing.Id);if(track==null)return;
        _store.SaveTracks([track with{BookmarkMilliseconds=checked((uint)Math.Max(0,_player.PositionSeconds*1000))}]);_tracks=_store.LoadTracks();
    }
    void SavePositionPeriodically(){if(DateTime.UtcNow-_lastPositionSave<TimeSpan.FromSeconds(10))return;_lastPositionSave=DateTime.UtcNow;SavePlaybackPosition();}
    void MarkEpisodePlayed(string id,bool played)
    {
        SaveEpisodes(Episodes().Select(x=>x.Id==id?x with{Played=played}:x));
        var changes=_tracks.Where(x=>x.EpisodeId==id).Select(x=>x with{PlayCount=played?Math.Max(1,x.PlayCount):0,BookmarkMilliseconds=played?0:x.BookmarkMilliseconds}).ToArray();_store.SaveTracks(changes);_tracks=_store.LoadTracks();
    }
    void PodcastOptions(System.Windows.Controls.StackPanel panel)
    {
        var keep=new TextBox{Text=_store.GetPreference("podcast-keep","10"),Width=65};
        panel.Children.Add(Row(T("每个订阅保留的已下载单集（0＝全部）",12),keep,Action("保存保留策略",()=>{if(!int.TryParse(keep.Text,out var count)||count<0||count>5000)throw new IOException("保留数量需要 0—5000。");_store.SetPreference("podcast-keep",count.ToString());return Task.CompletedTask;}),Action("清理已播放旧下载",CleanPodcastDownloads)));
        if(_downloads.Count>0)panel.Children.Add(Action("取消全部播客下载",()=>{foreach(var operation in _downloads.Values)operation.Cancel();return Task.CompletedTask;}));
    }
    Task CleanPodcastDownloads()
    {
        if(_downloads.Count>0)throw new IOException("请先取消或完成下载。");var keep=int.Parse(_store.GetPreference("podcast-keep","10"));if(keep==0){Notify("当前保留全部单集。");return Task.CompletedTask;}
        var episodes=Episodes();var candidates=episodes.GroupBy(x=>x.Feed).SelectMany(g=>g.Where(x=>File.Exists(x.DownloadPath)).OrderByDescending(x=>x.Published).Skip(keep).Where(x=>x.Played)).ToArray();
        if(candidates.Length==0){Notify("没有符合保留策略的旧下载。");return Task.CompletedTask;}
        if(MessageBox.Show(this,$"清理 {candidates.Length} 集已播放的电脑缓存？iPod 内容保留，单集可以重新下载。","播客保留策略",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return Task.CompletedTask;
        foreach(var episode in candidates){var path=DeviceAccess.SafePath(Path.Combine(_store.DataDirectory,"podcasts"),Path.GetFileName(episode.DownloadPath!));if(!Path.GetFullPath(path).Equals(Path.GetFullPath(episode.DownloadPath!),StringComparison.OrdinalIgnoreCase))throw new IOException("下载路径不属于播客缓存。");if(_player.Current?.Path==path)_player.Stop();File.Delete(path);}
        var ids=candidates.Select(x=>x.Id).ToHashSet();SaveEpisodes(episodes.Select(x=>ids.Contains(x.Id)?x with{DownloadPath=null}:x));Podcasts();return Task.CompletedTask;
    }
}
