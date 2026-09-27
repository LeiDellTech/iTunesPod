using System.IO;
using System.Net.Http;
using System.Windows;
using iTunesPod.Infrastructure;
namespace iTunesPod.App;
public sealed partial class MainWindow
{
    async Task OnlineCover(iTunesPod.Core.Track track,bool device)
    {
        if(_store.GetPreference("online-covers","false")!="true"){
            if(MessageBox.Show(this,"启用从你输入的 HTTP / HTTPS 地址下载封面？只有手动操作会联网，本地音乐不会上传。","网络封面",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            _store.SetPreference("online-covers","true");
        }
        var address=Prompt("输入封面图片地址");if(string.IsNullOrWhiteSpace(address))return;PodcastService.ValidateUrl(address);
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(30)};using var response=await client.GetAsync(address,HttpCompletionOption.ResponseHeadersRead,_lifetime.Token);response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>8*1024*1024)throw new IOException("封面超过 8 MiB。");await using var input=await response.Content.ReadAsStreamAsync(_lifetime.Token);using var output=new MemoryStream();var buffer=new byte[81920];int n;while((n=await input.ReadAsync(buffer,_lifetime.Token))>0){if(output.Length+n>8*1024*1024)throw new IOException("封面超过 8 MiB。");output.Write(buffer,0,n);}
        var folder=Path.Combine(_store.DataDirectory,"cover-downloads");Directory.CreateDirectory(folder);var path=Path.Combine(folder,Guid.NewGuid().ToString("N")+".img");await File.WriteAllBytesAsync(path,output.ToArray(),_lifetime.Token);
        try{NativeImages.Encode(path,false);await ChangeCover(track,device,false,path);}catch{File.Delete(path);throw;}
    }
}
