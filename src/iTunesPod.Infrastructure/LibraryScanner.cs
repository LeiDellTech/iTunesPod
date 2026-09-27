using System.Security.Cryptography;
using iTunesPod.Core;

namespace iTunesPod.Infrastructure;

public sealed class LibraryScanner(LibraryStore store)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".aiff", ".aif", ".ogg", ".wma", ".alac", ".mp4", ".m4v", ".mov" };

    public async Task<ImportResult> ImportAsync(IEnumerable<string> sources, IProgress<string>? progress, CancellationToken token)
    {
        var old = store.LoadTracks(); var byPath = old.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var byHash = old.Select(x => x.Hash).ToHashSet(StringComparer.Ordinal);
        var tracks = new List<Track>(); var errors = new List<string>(); var duplicates = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(source))
                    foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
                    { token.ThrowIfCancellationRequested(); if (Extensions.Contains(Path.GetExtension(file))) files.Add(Path.GetFullPath(file)); }
                else if (Extensions.Contains(Path.GetExtension(source))) files.Add(Path.GetFullPath(source));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"扫描失败：{source} · {e.Message}"); }
        }
        foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested(); progress?.Report(Path.GetFileName(path));
            try
            {
                if(path.Split(new[]{(char)92,(char)47}).Any(x=>x.Equals("iPod_Control",StringComparison.OrdinalIgnoreCase)))throw new IOException("设备媒体请在设备页浏览和导出，再导入电脑来源。");var before = new FileInfo(path); var length = before.Length; var modified = before.LastWriteTimeUtc;
                string hash;
                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                if (byPath.TryGetValue(path, out var previous) && previous.Hash == hash) { var user=store.GetPreference("cover-override:"+previous.Id);string? updatedCover=previous.CoverPath;if(user=="none")updatedCover=null;else if(File.Exists(user))updatedCover=user;else if(updatedCover==null||!Path.GetFullPath(updatedCover).StartsWith(Path.GetFullPath(Path.Combine(store.DataDirectory,"covers")),StringComparison.OrdinalIgnoreCase)){updatedCover=new[]{"cover.jpg","folder.jpg","front.jpg","cover.png","folder.png"}.Select(name=>Path.Combine(Path.GetDirectoryName(path)!,name)).FirstOrDefault(File.Exists);}if(updatedCover!=previous.CoverPath)tracks.Add(previous with{CoverPath=updatedCover});duplicates++; continue; }
                if(previous==null&&byHash.Contains(hash)){var renamed=old.FirstOrDefault(x=>x.Hash==hash&&!File.Exists(x.Path));if(renamed!=null)previous=renamed;else{duplicates++;continue;}}
                using var media = TagLib.File.Create(path);
                var tag = media.Tag;
                string? cover = null;
                if (tag.Pictures.FirstOrDefault() is { } picture && picture.Data.Count < 20 * 1024 * 1024)
                {
                    var coverDir = Path.Combine(store.DataDirectory, "covers"); Directory.CreateDirectory(coverDir);
                    cover = Path.Combine(coverDir, hash + ".img");
                    await System.IO.File.WriteAllBytesAsync(cover, picture.Data.Data, token);
                }
                if(cover==null){foreach(var name in new[]{"cover.jpg","folder.jpg","front.jpg","cover.png","folder.png"}){var candidate=Path.Combine(Path.GetDirectoryName(path)!,name);if(File.Exists(candidate)&&new FileInfo(candidate).Length<20*1024*1024){cover=candidate;break;}}}var userCover=previous==null?"":store.GetPreference("cover-override:"+previous.Id);if(userCover=="none")cover=null;else if(File.Exists(userCover))cover=userCover;var after = new FileInfo(path);
                if (after.Length != length || after.LastWriteTimeUtc != modified) throw new IOException("扫描时源文件发生变化，请重新导入。");
                static string Text(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
                var artist = Text(tag.FirstPerformer, "未知艺术家");
                var track = new Track(previous?.Id ?? Guid.NewGuid().ToString("N"), path, hash,
                    Text(tag.Title, Path.GetFileNameWithoutExtension(path)), artist, Text(tag.Album, "未知专辑"),
                    Text(tag.FirstAlbumArtist, artist), tag.Disc, tag.Track, tag.Year,
                    media.Properties.Duration.TotalSeconds, length, cover, tag.FirstGenre ?? "", (uint)media.Properties.AudioBitrate, (uint)media.Properties.AudioSampleRate, previous?.MediaType??(media.Properties.VideoWidth>0?2u:1u)){AddedUtc=previous?.AddedUtc??DateTimeOffset.UtcNow,LastPlayedUtc=previous?.LastPlayedUtc,PlayCount=previous?.PlayCount??0,Rating=previous?.Rating??0,SkipCount=previous?.SkipCount??0,BookmarkMilliseconds=previous?.BookmarkMilliseconds??0,PodcastFeed=previous?.PodcastFeed??"",EpisodeId=previous?.EpisodeId??"",EnclosureUrl=previous?.EnclosureUrl??"",Description=previous?.Description??"",AudioChannels=media.Properties.AudioChannels,Composer=tag.FirstComposer??"",Compilation=(media.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag)?.IsCompilation==true||(media.GetTag(TagLib.TagTypes.Apple) as TagLib.Mpeg4.AppleTag)?.IsCompilation==true};
                tracks.Add(track); byHash.Add(hash); byPath[path] = track;
            }
            catch (Exception e) when (e is not OperationCanceledException) { errors.Add($"{Path.GetFileName(path)}：{e.Message}"); }
        }
        token.ThrowIfCancellationRequested(); store.SaveTracks(tracks);
        return new(tracks, errors, duplicates);
    }
}

