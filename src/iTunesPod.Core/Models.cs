using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace iTunesPod.Core;

public sealed record Track(string Id, string Path, string Hash, string Title, string Artist,
    string Album, string AlbumArtist, uint Disc, uint Number, uint Year, double Seconds,
    long Bytes, string? CoverPath = null, string Genre = "", uint Bitrate = 0, uint SampleRate = 0, uint MediaType = 1, uint Rating = 0, uint PlayCount = 0)
{
    public int AudioChannels { get; init; }
    public bool Compilation { get; init; }
    public string Composer { get; init; } = "";
    public uint SkipCount { get; init; }
    public uint BookmarkMilliseconds { get; init; }
    public DateTimeOffset? AddedUtc { get; init; }
    public DateTimeOffset? LastPlayedUtc { get; init; }
    public string PodcastFeed { get; init; } = "";
    public string EpisodeId { get; init; } = "";
    public string EnclosureUrl { get; init; } = "";
    public string Description { get; init; } = "";
    public string Duration => TimeSpan.FromSeconds(Seconds).ToString(Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public string Status => File.Exists(Path) ? Id.StartsWith("device:") ? "在设备上" : "本地文件" : "来源失效";
    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public bool IsVideo => (MediaType & 2) != 0 || (MediaType & 32) != 0 || (MediaType & 64) != 0;
    public bool IsPodcast => (MediaType & 4) != 0;
    public string AlbumKey => JsonSerializer.Serialize(new[] { Album, Compilation ? "Various Artists" : AlbumArtist, Year.ToString() });
}

public sealed record Playlist(string Id, string Name, IReadOnlyList<string> TrackIds, bool IsSmart = false, bool IsSystem = false)
{ public SmartDefinition? SmartDefinition { get; init; } }
public sealed record SmartRule(int Field,string Comparison,string Value);
public sealed record SmartDefinition(bool MatchAll,IReadOnlyList<SmartRule> Rules,string Sort="标题",int Limit=0,string LimitUnit="首");
public sealed record ImportResult(IReadOnlyList<Track> Tracks, IReadOnlyList<string> Errors, int Duplicates);
public sealed record DeviceSession(string DeviceId, string Root, string Name, string Model,
    string MountEpoch, string Generation, long FreeBytes, long TotalBytes,
    IReadOnlyList<Track> Tracks, string ReadOnlyReason)
{
    public DeviceIdentity Identity { get; init; } = DeviceIdentity.Unknown;
    public IReadOnlyList<Playlist> Playlists { get; init; } = [];
    public string DatabaseFile { get; init; } = "iPod_Control/iTunes/iTunesDB";
    public string? ReadError { get; init; }
    public uint DatabaseVersion { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public byte[] SigningIdentity { get; init; } = [];
    public bool SignatureVerified { get; init; }
    public string? LibraryName { get; init; }
}
public sealed record IdentityEvidence(string Field, string Value, string Source);
public sealed record DeviceIdentity(string Family, string Generation, string ModelNumber, string Color,
    string Firmware, string SerialMasked, string HardwareId, string VolumeIdentity, string FileSystem,
    string Confidence, IReadOnlyList<IdentityEvidence> Evidence, IReadOnlyList<string> Warnings)
{
    public static DeviceIdentity Unknown => new("iPod", "", "", "", "", "", "", "", "", "未确定", [], []);
    public string DisplayModel => string.Join(" ",new[] {Family,Generation}.Where(x=>x.Length > 0));
}
public sealed record PlanOperation(string ObjectId, string Title, string Kind, string Reason, long Bytes);
public sealed record SyncPlan(string DeviceId, string MountEpoch, string Generation,
    IReadOnlyList<PlanOperation> Operations, IReadOnlyList<string> Blockers, long PeakBytes, string PlanHash);

public static class SyncPlanner
{
    // Metadata-only estimate; DeviceSync prepares and validates the concrete media/database before commit.
    public static SyncPlan Build(DeviceSession device, IEnumerable<Track> draft)
    {
        var selected = draft.DistinctBy(x => x.Id).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var operations = selected.Select(x => new PlanOperation(x.Id, x.Title,
            File.Exists(x.Path) ? "新增" : "冲突", File.Exists(x.Path)
                ? "用户明确加入草稿；设备编码与重复匹配尚待认证" : "E_SOURCE_MISSING：来源文件不存在，保留设备现有内容", x.Bytes)).ToArray();
        var payload = operations.Where(x => x.Kind == "新增").Sum(x => x.Bytes);
        var peak = checked(payload + Math.Max(64L * 1024 * 1024, (long)(payload * .05)));
        var blockers = new List<string>();
        if (!device.SignatureVerified) blockers.Add("E_SIGNATURE_UNVERIFIED：数据库签名尚未验证，不能提交。");
        if (operations.Any(x => x.Kind == "冲突")) blockers.Add("E_SOURCE_MISSING：请排除失效来源。");
        if (peak > device.FreeBytes) blockers.Add("E_SPACE_INSUFFICIENT：源媒体预估峰值超过设备可用空间。");
        var canonical = JsonSerializer.Serialize(new { device.DeviceId, device.MountEpoch, device.Generation, operations, peak });
        return new(device.DeviceId, device.MountEpoch, device.Generation, operations, blockers, peak,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    public static string? Validate(SyncPlan plan, DeviceSession current) =>
        plan.DeviceId != current.DeviceId || plan.MountEpoch != current.MountEpoch ? "E_DEVICE_CHANGED" :
        plan.Generation != current.Generation ? "E_EXTERNAL_CHANGE" : null;
}

