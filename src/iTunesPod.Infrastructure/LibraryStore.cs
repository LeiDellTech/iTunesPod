using System.Text.Json;
using iTunesPod.Core;
using Microsoft.Data.Sqlite;

namespace iTunesPod.Infrastructure;

public sealed class LibraryStore
{
    private readonly string _connection;
    public string DataDirectory { get; }
    public LibraryStore(string directory)
    {
        DataDirectory = directory;
        Directory.CreateDirectory(directory);
        _connection = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(directory, "library.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS tracks(id TEXT PRIMARY KEY, path TEXT UNIQUE COLLATE NOCASE, data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS playlists(id TEXT PRIMARY KEY, data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS drafts(device TEXT PRIMARY KEY, data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS preferences(id TEXT PRIMARY KEY, data TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public IReadOnlyList<Track> LoadTracks() => Load<Track>("tracks");
    public IReadOnlyList<Playlist> LoadPlaylists() => Load<Playlist>("playlists");
    public void SaveTracksAndPreferences(IEnumerable<Track> tracks,IReadOnlyDictionary<string,string> preferences){using var db=Open();using var transaction=db.BeginTransaction();foreach(var track in tracks){using var cmd=db.CreateCommand();cmd.Transaction=transaction;cmd.CommandText="INSERT INTO tracks(id,path,data) VALUES($id,$path,$data) ON CONFLICT(id) DO UPDATE SET path=$path,data=$data";cmd.Parameters.AddWithValue("$id",track.Id);cmd.Parameters.AddWithValue("$path",track.Path);cmd.Parameters.AddWithValue("$data",JsonSerializer.Serialize(track));cmd.ExecuteNonQuery();}foreach(var item in preferences){using var cmd=db.CreateCommand();cmd.Transaction=transaction;cmd.CommandText="INSERT INTO preferences(id,data) VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data";cmd.Parameters.AddWithValue("$id",item.Key);cmd.Parameters.AddWithValue("$data",item.Value);cmd.ExecuteNonQuery();}transaction.Commit();}
    private List<T> Load<T>(string table)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT data FROM {table} ORDER BY rowid";
        using var reader = cmd.ExecuteReader(); var items = new List<T>();
        while (reader.Read()) items.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
        return items;
    }
    public void SaveTracks(IEnumerable<Track> tracks)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var t in tracks)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO tracks(id,path,data) VALUES($id,$path,$data) ON CONFLICT(id) DO UPDATE SET path=$path,data=$data";
            cmd.Parameters.AddWithValue("$id", t.Id); cmd.Parameters.AddWithValue("$path", t.Path);
            cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(t)); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public void SavePlaylist(Playlist p)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO playlists(id,data) VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=$data";
        cmd.Parameters.AddWithValue("$id", p.Id); cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(p)); cmd.ExecuteNonQuery();
    }
    public void DeletePlaylist(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM playlists WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public string[] LoadDraft(string device)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT data FROM drafts WHERE device=$device";
        cmd.Parameters.AddWithValue("$device", device); return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<string[]>(json)! : [];
    }
    public void SaveDraft(string device, IEnumerable<string> ids)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO drafts(device,data) VALUES($device,$data) ON CONFLICT(device) DO UPDATE SET data=$data";
        cmd.Parameters.AddWithValue("$device", device); cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(ids)); cmd.ExecuteNonQuery();
    }
    public string GetPreference(string key,string fallback="")
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT data FROM preferences WHERE id=$id"; cmd.Parameters.AddWithValue("$id",key);
        return cmd.ExecuteScalar() as string ?? fallback;
    }
    public void SetPreference(string key,string value)
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="INSERT INTO preferences(id,data) VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET data=$value";
        cmd.Parameters.AddWithValue("$id",key); cmd.Parameters.AddWithValue("$value",value); cmd.ExecuteNonQuery();
    }
    public void RemoveFromIndex(IEnumerable<string> ids)
    {
        var set=ids.ToHashSet(); using var db=Open(); using var transaction=db.BeginTransaction();
        foreach(var id in set)
        { using var cmd=db.CreateCommand(); cmd.Transaction=transaction; cmd.CommandText="DELETE FROM tracks WHERE id=$id"; cmd.Parameters.AddWithValue("$id",id); cmd.ExecuteNonQuery(); }
        transaction.Commit();
        foreach(var playlist in LoadPlaylists()) SavePlaylist(playlist with { TrackIds=playlist.TrackIds.Where(id=>!set.Contains(id)).ToArray() });
    }
}
