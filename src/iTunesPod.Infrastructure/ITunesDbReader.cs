using System.Buffers.Binary;
using System.Text;
using System.IO.Compression;
using iTunesPod.Core;

namespace iTunesPod.Infrastructure;

/// <summary>Independent, bounded track-only reader. Unknown sections are skipped, never rewritten.</summary>
public sealed class ITunesDbReader
{
    public static byte[] NormalizeDatabase(byte[] bytes)
    {
        if(bytes.Length<16 || Encoding.ASCII.GetString(bytes,0,4)!="mhbd" || BitConverter.ToUInt32(bytes,12)!=2) return bytes;
        var header=BitConverter.ToUInt32(bytes,4);
        if(header<24 || header>bytes.Length) throw new InvalidDataException("E_DB_UNSUPPORTED：压缩数据库头无效。");
        // Some databases retain flag=2 after already being decompressed.
        if(bytes.Length>=header+4 && Encoding.ASCII.GetString(bytes,(int)header,4)=="mhsd") return bytes;
        using var input=new MemoryStream(bytes,(int)header,bytes.Length-(int)header);
        using var zlib=new ZLibStream(input,CompressionMode.Decompress);
        using var output=new MemoryStream(); output.Write(bytes,0,(int)header);
        var buffer=new byte[81920]; int count;
        while((count=zlib.Read(buffer))>0)
        {
            if(output.Length+count>128*1024*1024) throw new InvalidDataException("E_DB_UNSUPPORTED：解压后的数据库超过 128 MiB。");
            output.Write(buffer,0,count);
        }
        var result=output.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8,4),(uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12,4),1); return result;
    }
    public IReadOnlyList<Track> Read(byte[] bytes, string root)
    {
        bytes=NormalizeDatabase(bytes);
        var result = new List<Track>();var timeOffset=DeviceClock.CurrentOffset(root,bytes);
        int U32(int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4) throw new InvalidDataException("E_DB_UNSUPPORTED：字段越界。");
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            if (value > int.MaxValue) throw new InvalidDataException("E_DB_UNSUPPORTED：记录长度超限。");
            return (int)value;
        }
        (string Magic, int Header, int End) Chunk(int at, int limit)
        {
            if (at < 0 || at > limit - 12) throw new InvalidDataException("E_DB_UNSUPPORTED：记录头被截断。");
            var magic = Encoding.ASCII.GetString(bytes, at, 4); var header = U32(at + 4); var total = U32(at + 8);
            if (header < 12 || total < header || total > limit - at) throw new InvalidDataException("E_DB_UNSUPPORTED：记录长度无效。");
            return (magic, header, at + total);
        }
        var db = Chunk(0, bytes.Length);
        if (db.Magic != "mhbd" || db.Header < 24 || db.End != bytes.Length) throw new InvalidDataException("E_DB_UNSUPPORTED：非完整 iTunesDB。");
        var seen = new HashSet<int>(); var sections = U32(20); var sectionAt = db.Header;
        if (sections > 1000) throw new InvalidDataException("E_DB_UNSUPPORTED：过多分区。");
        for (var s = 0; s < sections; s++)
        {
            var section = Chunk(sectionAt, db.End);
            if (section.Magic != "mhsd" || section.Header < 16) throw new InvalidDataException("E_DB_UNSUPPORTED：分区头无效。");
            if (U32(sectionAt + 12) == 1)
            {
                var listAt = sectionAt + section.Header;
                // mhlt's third field is a count, not total length.
                if (listAt > section.End - 12 || Encoding.ASCII.GetString(bytes, listAt, 4) != "mhlt") throw new InvalidDataException("E_DB_UNSUPPORTED：缺少曲目列表。");
                var listHeader = U32(listAt + 4); var count = U32(listAt + 8);
                if (listHeader < 12 || listHeader > section.End - listAt || count > 200000) throw new InvalidDataException("E_DB_UNSUPPORTED：曲目列表超限。");
                var at = listAt + listHeader;
                for (var t = 0; t < count; t++)
                {
                    var item = Chunk(at, section.End);
                    if (item.Magic != "mhit" || item.Header < 0x9c) throw new InvalidDataException("E_DB_UNSUPPORTED：不支持的曲目记录。");
                    var id = U32(at + 16); if (!seen.Add(id)) throw new InvalidDataException("E_DB_UNSUPPORTED：重复曲目 ID。");
                    var strings = new Dictionary<int, string>(); var children = U32(at + 12); var childAt = at + item.Header;
                    if (children > 10000) throw new InvalidDataException("E_DB_UNSUPPORTED：曲目子记录超限。");
                    for (var c = 0; c < children; c++)
                    {
                        var child = Chunk(childAt, item.End);
                        if (child.Magic != "mhod" || child.Header < 24) throw new InvalidDataException("E_DB_UNSUPPORTED：元数据记录无效。");
                        var type = U32(childAt + 12);
                        if (type is 1 or 2 or 3 or 4 or 5 or 12 or 14 or 20 or 22)
                        {
                            if (child.End - childAt < 40) throw new InvalidDataException("E_DB_UNSUPPORTED：字符串头被截断。");
                            var encoding = U32(childAt + 24); var size = U32(childAt + 28);
                            if (size > child.End - childAt - 40 || encoding > 2 || (encoding != 2 && size % 2 != 0)) throw new InvalidDataException("E_DB_UNSUPPORTED：字符串长度/编码无效。");
                            var codec = encoding == 2 ? new UTF8Encoding(false, true) : new UnicodeEncoding(false, false, true) as Encoding;
                            strings[type] = codec.GetString(bytes, childAt + 40, size).TrimEnd('\0');
                        }
                        if(type is 15 or 16)strings[type]=new UTF8Encoding(false,true).GetString(bytes,childAt+child.Header,child.End-childAt-child.Header).TrimEnd((char)0);childAt = child.End;
                    }
                    if (childAt != item.End) throw new InvalidDataException("E_DB_UNSUPPORTED：曲目尾部未解析。");
                    string Text(int type, string fallback) => strings.TryGetValue(type, out var text) && !string.IsNullOrWhiteSpace(text) ? text : fallback;
                    var mediaPath = Text(2, ""); var path = string.IsNullOrEmpty(mediaPath) ? "" : DeviceAccess.SafePath(root, mediaPath.TrimStart(':').Replace(':', Path.DirectorySeparatorChar));
                    var artist = Text(4, "未知艺术家");
                    result.Add(new("device:" + id, path, "", Text(1, "未命名曲目"), artist, Text(3, "未知专辑"), Text(22, artist),
                        (uint)U32(at + 0x5c), (uint)U32(at + 0x2c), (uint)U32(at + 0x34), U32(at + 0x28) / 1000d, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+0x24)),
                        Genre:Text(5,""), Bitrate:(uint)U32(at+0x38), MediaType:item.Header>=0xd4 ? (uint)U32(at+0xd0) : 1, Rating:bytes[at+0x1f]/20u, PlayCount:(uint)U32(at+0x50), SampleRate:BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+0x3c))>>16){Compilation=bytes[at+0x1e]!=0,Composer=Text(12,""),EpisodeId=Text(20,""),Description=Text(14,""),PodcastFeed=Text(16,""),EnclosureUrl=Text(15,""),SkipCount=item.Header>=0xa0?(uint)U32(at+0x9c):0,BookmarkMilliseconds=(uint)U32(at+0x6c),AddedUtc=DeviceClock.Read(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+104)),timeOffset),LastPlayedUtc=DeviceClock.Read(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+88)),timeOffset)});
                    at = item.End;
                }
                if (at != section.End) throw new InvalidDataException("E_DB_UNSUPPORTED：曲目分区尾部不一致。");
            }
            sectionAt = section.End;
        }
        if (sectionAt != db.End) throw new InvalidDataException("E_DB_UNSUPPORTED：数据库分区计数不一致。");
        return result;
    }

    public IReadOnlyList<Playlist> ReadPlaylists(byte[] source)
    {
        var data=NormalizeDatabase(source); var output=new Dictionary<string,Playlist>();
        int U(int at) { if(at<0 || at>data.Length-4) throw new InvalidDataException("列表字段越界。"); var n=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at,4)); if(n>int.MaxValue) throw new InvalidDataException("列表长度超限。"); return (int)n; }
        (string Magic,int Header,int End) Chunk(int at,int limit)
        {
            if(at>limit-12) throw new InvalidDataException("列表记录被截断。"); var h=U(at+4); var n=U(at+8);
            if(h<12 || n<h || n>limit-at) throw new InvalidDataException("列表记录长度无效。");
            return (Encoding.ASCII.GetString(data,at,4),h,at+n);
        }
        if(data.Length<24) throw new InvalidDataException("列表数据库被截断。");
        var at=U(4); var sections=U(20);
        for(var s=0;s<sections;s++)
        {
            var section=Chunk(at,data.Length); if(section.Header<16) throw new InvalidDataException("列表分区头无效。"); var type=U(at+12);
            if(type is 2 or 3)
            {
                var listAt=at+section.Header;
                if(listAt>section.End-12 || Encoding.ASCII.GetString(data,listAt,4)!="mhlp") throw new InvalidDataException("列表分区缺少 mhlp。");
                var header=U(listAt+4); var count=U(listAt+8); if(header<12||header>section.End-listAt||count>50000) throw new InvalidDataException("列表计数无效。");
                var position=listAt+header;
                for(var i=0;i<count;i++)
                {
                    var playlist=Chunk(position,section.End); if(playlist.Magic!="mhyp" || playlist.Header<36) throw new InvalidDataException("列表头无效。");
                    var id=BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position+28,8)).ToString("X16");
                    var isSystem=data[position+20]!=0; var name="未命名播放列表"; var smart=false; byte[]? smartPreferences=null,smartRules=null; var refs=new List<string>();
                    var child=position+playlist.Header;
                    while(child<playlist.End)
                    {
                        var entry=Chunk(child,playlist.End);
                        if(entry.Magic=="mhod" && entry.Header>=24)
                        {
                            var kind=U(child+12); if(kind is 50 or 51) smart=true;if(kind==50)smartPreferences=data.AsSpan(child,entry.End-child).ToArray();if(kind==51)smartRules=data.AsSpan(child,entry.End-child).ToArray();
                            if(kind==1)
                            {
                                if(entry.End-child<40) throw new InvalidDataException("列表名称被截断。"); var length=U(child+28); var encoding=U(child+24);
                                if(length>entry.End-child-40||encoding>2||(encoding!=2 && length%2!=0)) throw new InvalidDataException("列表名称长度无效。");
                                name=(encoding==2 ? new UTF8Encoding(false,true) as Encoding : new UnicodeEncoding(false,false,true)).GetString(data,child+40,length).TrimEnd('\0');
                            }
                        }
                        else if(entry.Magic=="mhip" && entry.Header>=28)
                        {
                            var track=U(child+24); if(track!=0) refs.Add("device:"+track);
                        }
                        child=entry.End;
                    }
                    // Section 3 is the newer hierarchy; it supersedes duplicated section-2 IDs.
                    output[id]=new("device-playlist:"+id,name,refs,smart,isSystem){SmartDefinition=smartPreferences!=null&&smartRules!=null?SmartPlaylistBinary.Decode(smartPreferences,smartRules):null};
                    position=playlist.End;
                }
            }
            at=section.End;
        }
        return output.Values.ToArray();
    }
}
