using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace Shrink.Desktop;

public sealed class Journal
{
    private readonly string connectionString;
    private readonly object gate = new();
    public Journal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS commands(id TEXT PRIMARY KEY,request TEXT NOT NULL,state TEXT NOT NULL,result TEXT);
            CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT,body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,body TEXT NOT NULL);
            UPDATE commands SET state='unknown',result='{"ok":false,"error":"Client restarted during execution; inspect before retry"}' WHERE state='executing';
            """);
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    private void Execute(string sql)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
    public (bool Fresh, JsonNode? Result) Begin(string id, JsonObject request)
    {
        lock (gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT request,result FROM commands WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using (var reader = cmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (!JsonNode.DeepEquals(JsonNode.Parse(reader.GetString(0)), request))
                        throw new InvalidOperationException("Command ID reused with different content");
                    return (false, reader.IsDBNull(1) ? new JsonObject { ["ok"] = true, ["state"] = "executing" } : JsonNode.Parse(reader.GetString(1)));
                }
            }
            cmd.CommandText = "INSERT INTO commands(id,request,state) VALUES($id,$body,'executing')";
            cmd.Parameters.AddWithValue("$body", request.ToJsonString()); cmd.ExecuteNonQuery();
            return (true, null);
        }
    }
    public void Complete(string id, JsonNode result)
    {
        lock (gate)
        {
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE commands SET state='done',result=$body WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$body", result.ToJsonString()); cmd.ExecuteNonQuery();
        }
    }
    public long Emit(JsonObject body)
    {
        lock (gate)
        {
            body["at"] = DateTimeOffset.UtcNow.ToString("O");
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO events(body) VALUES($body); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$body", body.ToJsonString()); return (long)cmd.ExecuteScalar()!;
        }
    }
    public List<JsonObject> Events(long after)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq,body FROM events WHERE seq>$seq ORDER BY seq LIMIT 100";
        cmd.Parameters.AddWithValue("$seq", after); using var r = cmd.ExecuteReader();
        var result = new List<JsonObject>();
        while (r.Read()) result.Add(new JsonObject { ["type"] = "event", ["seq"] = r.GetInt64(0), ["event"] = JsonNode.Parse(r.GetString(1)) });
        return result;
    }
    public void Session(JsonObject body)
    {
        lock (gate)
        {
            using var c = Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions(id,body) VALUES($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body";
            cmd.Parameters.AddWithValue("$id", body["id"]!.GetValue<string>());
            cmd.Parameters.AddWithValue("$body", body.ToJsonString()); cmd.ExecuteNonQuery();
        }
    }
    public JsonArray Sessions()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT body FROM sessions"; using var r = cmd.ExecuteReader();
        var result = new JsonArray();
        while (r.Read()) result.Add(JsonNode.Parse(r.GetString(0)));
        return result;
    }
}
