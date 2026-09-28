using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ValheimAdmin.Agent.Data;

namespace ValheimAdmin.Agent.Snapshots;

public sealed record SnapshotRow(long Id, long Ts, long CharacterId, string Player, string? Host, string Trigger, bool Unchanged, JsonObject? Summary);

public sealed record CharacterRow(long CharacterId, string Player, string? Host, int Count, long LastTs);

/// <summary>
/// Snapshot rows point at deduplicated, deflated content (snapshot_data), so an unchanged
/// character costs one small row per world save.
/// </summary>
public sealed class SnapshotStore(Db db, AgentConfig config, ILogger<SnapshotStore> log)
{
    public long Add(JsonObject snapshot, string trigger, string? host, long ts)
    {
        long characterId = snapshot["characterId"]?.GetValue<long>() ?? 0;
        string player = SnapshotLogic.Str(snapshot, "name") ?? "?";
        string hash = SnapshotLogic.ContentHash(snapshot);
        string summary = SnapshotLogic.Summary(snapshot).ToJsonString();

        using var c = db.Open();
        using var tx = c.BeginTransaction();
        string? previous = (string?)Db.Command(c, "SELECT hash FROM snapshots WHERE character_id=$c ORDER BY ts DESC, id DESC LIMIT 1",
            ("$c", characterId)).ExecuteScalar();
        bool unchanged = previous == hash;

        if (Convert.ToInt64(Db.Command(c, "SELECT COUNT(*) FROM snapshot_data WHERE hash=$h", ("$h", hash)).ExecuteScalar()) == 0)
        {
            Db.Exec(c, "INSERT INTO snapshot_data(hash, data) VALUES($h, $d)",
                ("$h", hash), ("$d", Compress(snapshot.ToJsonString())));
        }
        using var insert = Db.Command(c, """
            INSERT INTO snapshots(ts, character_id, player, host, trigger, hash, unchanged, summary)
            VALUES($ts, $c, $p, $host, $t, $h, $u, $s); SELECT last_insert_rowid();
            """,
            ("$ts", ts), ("$c", characterId), ("$p", player), ("$host", host), ("$t", trigger),
            ("$h", hash), ("$u", unchanged ? 1 : 0), ("$s", summary));
        long id = (long)insert.ExecuteScalar()!;
        tx.Commit();
        return id;
    }

    public List<CharacterRow> Characters() => db.Query("""
        SELECT s.character_id, s.player, s.host, g.cnt, g.last
        FROM (SELECT character_id, COUNT(*) cnt, MAX(ts) last FROM snapshots GROUP BY character_id) g
        JOIN snapshots s ON s.id = (SELECT id FROM snapshots WHERE character_id=g.character_id ORDER BY ts DESC, id DESC LIMIT 1)
        ORDER BY g.last DESC
        """,
        r => new CharacterRow(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3), r.GetInt64(4)));

    public List<SnapshotRow> List(long characterId, int limit = 500) => db.Query("""
        SELECT id, ts, character_id, player, host, trigger, unchanged, summary FROM snapshots
        WHERE character_id=$c ORDER BY ts DESC, id DESC LIMIT $l
        """, Map, ("$c", characterId), ("$l", limit));

    public SnapshotRow? Row(long id) => db.Query("""
        SELECT id, ts, character_id, player, host, trigger, unchanged, summary FROM snapshots WHERE id=$id
        """, Map, ("$id", id)).FirstOrDefault();

    public JsonObject? Content(long id)
    {
        var blob = db.Query("SELECT d.data FROM snapshots s JOIN snapshot_data d ON d.hash = s.hash WHERE s.id=$id",
            r => (byte[])r[0], ("$id", id)).FirstOrDefault();
        return blob == null ? null : JsonNode.Parse(Decompress(blob)) as JsonObject;
    }

    private static SnapshotRow Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5), r.GetInt64(6) != 0, r.IsDBNull(7) ? null : JsonNode.Parse(r.GetString(7)) as JsonObject);

    public int ApplyRetention()
    {
        var rows = db.Query("SELECT id, character_id, ts FROM snapshots", r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
        var delete = SnapshotLogic.Retention(rows, DateTimeOffset.UtcNow,
            config.Snapshots.KeepAllDays, config.Snapshots.KeepDailyDays, TimeZoneInfo.Local);
        if (delete.Count == 0) return 0;

        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (long id in delete)
            Db.Exec(c, "DELETE FROM snapshots WHERE id=$id", ("$id", id));
        Db.Exec(c, "DELETE FROM snapshot_data WHERE hash NOT IN (SELECT DISTINCT hash FROM snapshots)");
        tx.Commit();
        log.LogInformation("Retention removed {Count} snapshots", delete.Count);
        return delete.Count;
    }

    private static byte[] Compress(string json)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal))
            deflate.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    private static string Decompress(byte[] data)
    {
        using var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
