using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using iTunesPod.Core;

namespace iTunesPod.Infrastructure;

public sealed class DeviceAccess
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> Epochs=new(StringComparer.OrdinalIgnoreCase);
    public static void InvalidateMount(string root){foreach(var key in Epochs.Keys.Where(x=>x.StartsWith(Path.GetFullPath(root)+"|",StringComparison.OrdinalIgnoreCase)))Epochs.TryRemove(key,out _);}
    public static string SafePath(string root, string relative)
    {
        if(relative.Split(new[]{(char)92,(char)47}).Any(x=>x is "." or ".."||x.EndsWith((char)32)||x.EndsWith((char)46)||x.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new InvalidDataException("E_PATH_UNSAFE：路径含无效名称或穿越段。");var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!target.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative))
            throw new InvalidDataException("E_PATH_UNSAFE：路径超出设备目录。");
        var walk = new DirectoryInfo(basePath);
        while (walk != null)
        {
            if (walk.Exists && (walk.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("E_PATH_UNSAFE：设备根路径含重解析点。");
            walk = walk.Parent;
        }
        var current = basePath;
        foreach (var part in Path.GetRelativePath(basePath, target).Split(Path.DirectorySeparatorChar))
        {
            var name=part.Split('.')[0].TrimEnd(' ','.');
            if(System.Text.RegularExpressions.Regex.IsMatch(name,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new InvalidDataException("E_PATH_UNSAFE：设备路径含 Windows 保留名称。");
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("E_PATH_UNSAFE：不跟随设备链接。");
        }
        return target;
    }

    public IReadOnlyList<string> Discover()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.IsReady && Directory.Exists(SafePath(drive.RootDirectory.FullName, "iPod_Control"))) roots.Add(drive.RootDirectory.FullName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* A locked volume is not a connected readable device. */ }
        }
        return roots;
    }

    public async Task<DeviceSession> InspectAsync(string root, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        var control = SafePath(root, "iPod_Control");
        if (!Directory.Exists(control)) throw new InvalidDataException("E_DEVICE_UNKNOWN：目录中没有 iPod_Control。");
        var dbPath = SafePath(root, "iPod_Control/iTunes/iTunesDB");
        var cdb = SafePath(root, "iPod_Control/iTunes/iTunesCDB");
        var infoPath = SafePath(root, "iPod_Control/Device/SysInfo");
        var info = File.Exists(infoPath) ? await File.ReadAllTextAsync(infoPath, token) : "";
        var plain=DeviceIdentityResolver.ReadText(info);
        var extended=new Dictionary<string,string>();
        var extendedPath=SafePath(root,"iPod_Control/Device/SysInfoExtended");
        if(File.Exists(extendedPath) && new FileInfo(extendedPath).Length<2*1024*1024)
        {
            try { extended=DeviceIdentityResolver.ReadPlist(await File.ReadAllTextAsync(extendedPath,token)); }
            catch(System.Xml.XmlException) { /* Invalid cached plist does not override live hardware identity. */ }
        }
        var hardware=WindowsDeviceProbe.Inspect(root);
        var identity=DeviceIdentityResolver.Resolve(plain,extended,hardware);
        var serial=DeviceIdentityResolver.SerialFrom(plain,extended,hardware);
        var identityValue=hardware.HardwareKey.Length>0 ? "hardware:"+hardware.HardwareKey : serial.Length>0 ? "serial:"+serial : hardware.VolumeId;
        var id=identityValue.Length>0 ? "ipod:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityValue))) : "unverified:"+Guid.NewGuid().ToString("N");
        var reason = "可浏览和试听现有内容。此型号的数据库写入与恢复尚未通过本项目验证。";
        byte[] bytes = [];
        IReadOnlyList<Track> tracks = [];
        IReadOnlyList<Playlist> playlists=[]; string? readError=null;
        var relativeDatabase="iPod_Control/iTunes/iTunesDB";
        if(!File.Exists(dbPath) && File.Exists(cdb)) { dbPath=cdb; relativeDatabase="iPod_Control/iTunes/iTunesCDB"; }
        if (File.Exists(dbPath))
        {
            if (new FileInfo(dbPath).Length > 128 * 1024 * 1024) throw new InvalidDataException("E_DB_UNSUPPORTED：数据库超过首版 128 MiB 读取限制。");
            bytes = await File.ReadAllBytesAsync(dbPath, token);
            try
            {
                var reader=new ITunesDbReader(); tracks=reader.Read(bytes,root).Select(track=>File.Exists(track.Path)?track with{Bytes=new FileInfo(track.Path).Length}:track).ToArray(); playlists=reader.ReadPlaylists(bytes);
            }
            catch(Exception e) when(e is InvalidDataException or DecoderFallbackException) { readError=e.Message; }
        }
        else readError="设备已识别，但未找到可读取的 iTunesDB / iTunesCDB。";
        var signingIdentity=Array.Empty<byte>();var signatureVerified=false;
        var guid=hardware.FirewireGuid.Length>0?hardware.FirewireGuid:plain.GetValueOrDefault("FirewireGuid", "").Replace("0x", "",StringComparison.OrdinalIgnoreCase);
        if(System.Text.RegularExpressions.Regex.IsMatch(guid,"^[0-9a-fA-F]{16}$")){signingIdentity=Convert.FromHexString(guid);if(bytes.Length>=108&&BitConverter.ToUInt16(bytes,48)==1)signatureVerified=DeviceChecksum.VerifyHash58(bytes,signingIdentity);}
        if(signatureVerified&&identity.DisplayModel.Replace(" ","").Contains("Nano第4代")&&bytes.Length>=20&&BitConverter.ToUInt32(bytes,16)==115)reason="数据库签名已核验。同步、备份与恢复已通过电脑副本验证；设备实际播放与显示仍需验收。";var drive = new DriveInfo(Path.GetPathRoot(root)!);
        var libraryName=playlists.FirstOrDefault(x=>x.IsSystem)?.Name;
        var name=!string.IsNullOrWhiteSpace(libraryName) ? libraryName : string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "iPod" : drive.VolumeLabel;
        return new(id, root, name, identity.DisplayModel, Epochs.GetOrAdd(root+"|"+id,_=>Guid.NewGuid().ToString("N")),
            Convert.ToHexString(SHA256.HashData(bytes)), drive.AvailableFreeSpace, drive.TotalSize, tracks, reason)
        { Identity=identity, Playlists=playlists, ReadError=readError, DatabaseFile=relativeDatabase,
            DatabaseVersion=bytes.Length>=20 ? BitConverter.ToUInt32(bytes,16) : 0, LibraryName=libraryName, SigningIdentity=signingIdentity, SignatureVerified=signatureVerified };
    }

    public async Task<string> SnapshotAsync(DeviceSession session, string destination, CancellationToken token = default)
    {
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var deviceRoot = Path.GetFullPath(session.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (destinationRoot.StartsWith(deviceRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("快照必须保存到设备目录之外。");
        var files = new[] { session.DatabaseFile, "iPod_Control/Artwork/ArtworkDB", "iPod_Control/Device/SysInfo", "iPod_Control/Device/SysInfoExtended" };
        var folder = SafePath(destinationRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder); var manifest = new List<object>();
        try
        {
            foreach (var relative in files)
            {
                var source = SafePath(session.Root, relative); if (!File.Exists(source)) continue;
                var target = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                { await input.CopyToAsync(output, token); await output.FlushAsync(token); }
                await using var copied = File.OpenRead(target); var hash = Convert.ToHexString(await SHA256.HashDataAsync(copied, token));
                await using var original = File.OpenRead(source); var originalHash = Convert.ToHexString(await SHA256.HashDataAsync(original, token));
                if (hash != originalHash || (relative == session.DatabaseFile && hash != session.Generation)) throw new IOException("E_EXTERNAL_CHANGE：读取过程中设备内容发生变化。");
                manifest.Add(new { RelativePath = relative, Hash = hash, Bytes = copied.Length });
            }
            if (!File.Exists(Path.Combine(folder, files[0]))) throw new IOException("设备数据库已断开。");
            await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new
            { Kind = "DiagnosticMetadataSnapshot", session.DeviceId, session.Model, session.Generation,
                CreatedUtc = DateTime.UtcNow, Files = manifest, Limitations = "仅指定元数据；不含媒体、ithmb、照片或派生数据库，不可作为完整恢复备份。首版不提供恢复写入。" }, new JsonSerializerOptions { WriteIndented = true }), token);
            return folder;
        }
        catch { await File.WriteAllTextAsync(Path.Combine(folder, "INCOMPLETE.txt"), "快照未完成，不能用于恢复。", CancellationToken.None); throw; }
    }
}


