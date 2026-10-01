using System.Data;
using MeshCoreMqtt.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MeshCoreMqtt.Api.Services;

public static class MapTables
{
    public static Task Ensure(AppDb db) => db.Database.ExecuteSqlRawAsync("""
        DO $mig$
        BEGIN
          CREATE TABLE IF NOT EXISTS map_nodes (
            "BrokerNodeId" uuid NOT NULL,
            "Tunnel" character varying(80) NOT NULL,
            "PublicKey" character varying(64) NOT NULL,
            "Name" character varying(120) NOT NULL,
            "Latitude" double precision NULL,
            "Longitude" double precision NULL,
            "Repeater" boolean NOT NULL,
            "Mqtt" boolean NOT NULL,
            "Seen" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_map_nodes" PRIMARY KEY ("BrokerNodeId", "Tunnel", "PublicKey")
          );
          CREATE INDEX IF NOT EXISTS "IX_map_nodes_Seen" ON map_nodes ("Seen");
          CREATE TABLE IF NOT EXISTS map_links (
            "BrokerNodeId" uuid NOT NULL,
            "Tunnel" character varying(80) NOT NULL,
            "FromKey" character varying(64) NOT NULL,
            "ToKey" character varying(64) NOT NULL,
            "Seen" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_map_links" PRIMARY KEY ("BrokerNodeId", "Tunnel", "FromKey", "ToKey")
          );
          CREATE INDEX IF NOT EXISTS "IX_map_links_Seen" ON map_links ("Seen");
        END
        $mig$;
        """);
}

public sealed partial class StatsStore
{
    public async Task LoadMapAsync(AppDb db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - MapKeep;
        var nodes = await ReadNodes(db, cutoff, ct);
        var links = await ReadLinks(db, cutoff, ct);
        lock (_nodes)
        {
            foreach (var row in nodes)
            {
                _mapNodes[(row.BrokerNodeId, row.Tunnel, row.PublicKey)] = new MapNode
                {
                    Name = row.Name,
                    Latitude = row.Latitude,
                    Longitude = row.Longitude,
                    Repeater = row.Repeater,
                    Mqtt = row.Mqtt,
                    Seen = row.Seen
                };
            }

            foreach (var row in links)
                _mapLinks[(row.BrokerNodeId, row.Tunnel, row.FromKey, row.ToKey)] = row.Seen;
        }
    }

    public async Task SaveMapAsync(AppDb db, CancellationToken ct)
    {
        List<StoredNode> nodes;
        List<StoredLink> links;
        var prune = false;
        var cutoff = DateTime.UtcNow - MapKeep;
        lock (_nodes)
        {
            nodes = new List<StoredNode>(_dirtyNodes.Count);
            foreach (var key in _dirtyNodes)
            {
                if (_mapNodes.TryGetValue(key, out var node))
                    nodes.Add(new StoredNode(key.NodeId, key.Tunnel, key.PublicKey, node.Name, node.Latitude, node.Longitude, node.Repeater, node.Mqtt, Utc(node.Seen)));
            }

            links = new List<StoredLink>(_dirtyLinks.Count);
            foreach (var key in _dirtyLinks)
            {
                if (_mapLinks.TryGetValue(key, out var seen))
                    links.Add(new StoredLink(key.NodeId, key.Tunnel, key.From, key.To, Utc(seen)));
            }

            _dirtyNodes.Clear();
            _dirtyLinks.Clear();
            if (DateTime.UtcNow - _mapPruned >= TimeSpan.FromMinutes(1))
            {
                _mapPruned = DateTime.UtcNow;
                prune = true;
                DropExpired(cutoff);
            }
        }

        if (nodes.Count == 0 && links.Count == 0 && !prune)
            return;

        try
        {
            foreach (var row in nodes)
                await UpsertNode(db, row, ct);
            foreach (var row in links)
                await UpsertLink(db, row, ct);
            if (prune)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM map_nodes WHERE "Seen" < {cutoff}""", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM map_links WHERE "Seen" < {cutoff}""", ct);
            }
        }
        catch
        {
            lock (_nodes)
            {
                foreach (var row in nodes)
                    _dirtyNodes.Add((row.BrokerNodeId, row.Tunnel, row.PublicKey));
                foreach (var row in links)
                    _dirtyLinks.Add((row.BrokerNodeId, row.Tunnel, row.FromKey, row.ToKey));
            }

            throw;
        }
    }

    static async Task UpsertNode(AppDb db, StoredNode row, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO map_nodes ("BrokerNodeId", "Tunnel", "PublicKey", "Name", "Latitude", "Longitude", "Repeater", "Mqtt", "Seen")
            VALUES ({row.BrokerNodeId}, {row.Tunnel}, {row.PublicKey}, {row.Name}, {row.Latitude}, {row.Longitude}, {row.Repeater}, {row.Mqtt}, {row.Seen})
            ON CONFLICT ("BrokerNodeId", "Tunnel", "PublicKey") DO UPDATE SET
              "Name" = EXCLUDED."Name",
              "Latitude" = EXCLUDED."Latitude",
              "Longitude" = EXCLUDED."Longitude",
              "Repeater" = EXCLUDED."Repeater",
              "Mqtt" = EXCLUDED."Mqtt",
              "Seen" = EXCLUDED."Seen"
            """, ct);
    }

    static async Task UpsertLink(AppDb db, StoredLink row, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO map_links ("BrokerNodeId", "Tunnel", "FromKey", "ToKey", "Seen")
            VALUES ({row.BrokerNodeId}, {row.Tunnel}, {row.FromKey}, {row.ToKey}, {row.Seen})
            ON CONFLICT ("BrokerNodeId", "Tunnel", "FromKey", "ToKey") DO UPDATE SET
              "Seen" = EXCLUDED."Seen"
            """, ct);
    }

    static async Task<List<StoredNode>> ReadNodes(AppDb db, DateTime cutoff, CancellationToken ct)
    {
        var rows = new List<StoredNode>();
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "BrokerNodeId", "Tunnel", "PublicKey", "Name", "Latitude", "Longitude", "Repeater", "Mqtt", "Seen"
                FROM map_nodes
                WHERE "Seen" >= @cutoff
                ORDER BY "Seen" DESC
                LIMIT 8000
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "cutoff";
            parameter.Value = cutoff;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new StoredNode(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7),
                    Utc(reader.GetDateTime(8))));
            }
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }

        return rows;
    }

    static async Task<List<StoredLink>> ReadLinks(AppDb db, DateTime cutoff, CancellationToken ct)
    {
        var rows = new List<StoredLink>();
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "BrokerNodeId", "Tunnel", "FromKey", "ToKey", "Seen"
                FROM map_links
                WHERE "Seen" >= @cutoff
                ORDER BY "Seen" DESC
                LIMIT 12000
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "cutoff";
            parameter.Value = cutoff;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new StoredLink(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    Utc(reader.GetDateTime(4))));
            }
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }

        return rows;
    }

    static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    readonly record struct StoredNode(
        Guid BrokerNodeId,
        string Tunnel,
        string PublicKey,
        string Name,
        double? Latitude,
        double? Longitude,
        bool Repeater,
        bool Mqtt,
        DateTime Seen);

    readonly record struct StoredLink(
        Guid BrokerNodeId,
        string Tunnel,
        string FromKey,
        string ToKey,
        DateTime Seen);
}
