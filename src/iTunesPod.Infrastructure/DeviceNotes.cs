using System.Text;
namespace iTunesPod.Infrastructure;
public static class DeviceNotes
{
    public static string Read(string path){try{return new System.Text.UTF8Encoding(false,true).GetString(File.ReadAllBytes(path)).TrimStart((char)0xfeff);}catch(System.Text.DecoderFallbackException){throw new IOException("旧备忘录编码未知，请原字节导出后转换为 UTF-8，再导入新文件。");}}

    public static FileChange Save(string name,string text){if(string.IsNullOrWhiteSpace(name)||name.Length>100||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||name is "." or ".."||name.EndsWith('.')||name.EndsWith(' '))throw new IOException("备忘录文件名无效。");var bytes=new UTF8Encoding(true,true).GetPreamble().Concat(new UTF8Encoding(false,true).GetBytes(text.Replace("\r\n","\n").Replace("\n","\r\n"))).ToArray();if(bytes.Length>4096)throw new IOException("Nano 4 备忘录每篇最多 4096 字节，请缩短内容。");return new("Notes/"+name+(Path.HasExtension(name)?"":".txt"),bytes);}
}
