using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshCoreMqtt.Api.Services;

[Flags]
public enum LiveWatch
{
    Tree = 1,
    Activity = 2,
    Nodes = 4
}

public sealed class LiveHub(IServiceScopeFactory scopes, ILogger<LiveHub> log)
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    sealed record Session(WebSocket Socket, Guid UserId, LiveWatch Watch, CancellationToken Aborted);

    readonly object _gate = new();
    readonly List<Session> _sessions = [];
    CancellationTokenSource? _debounce;

    public static LiveWatch ParseWatch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return LiveWatch.Tree;
        var watch = (LiveWatch)0;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            watch |= part.ToLowerInvariant() switch
            {
                "tree" => LiveWatch.Tree,
                "activity" => LiveWatch.Activity,
                "nodes" => LiveWatch.Nodes,
                _ => (LiveWatch)0
            };
        }

        return watch == 0 ? LiveWatch.Tree : watch;
    }

    public void MarkChanged()
    {
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            var token = _debounce.Token;
            _ = DebounceAsync(token);
        }
    }

    async Task DebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            await BroadcastAsync(token);
        }
        catch (OperationCanceledException)
        {
            /* новое изменение перезапустило таймер */
        }
    }

    public async Task RunSession(WebSocket socket, Guid userId, LiveWatch watch, CancellationToken ct)
    {
        var session = new Session(socket, userId, watch, ct);
        lock (_gate)
            _sessions.Add(session);
        try
        {
            await PushSessionAsync(session, ct);
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            /* клиент отключился */
        }
        catch (WebSocketException ex)
        {
            log.LogDebug(ex, "WebSocket закрыт");
        }
        finally
        {
            lock (_gate)
                _sessions.Remove(session);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch
                {
                    /* уже закрыт */
                }
            }
        }
    }

    async Task BroadcastAsync(CancellationToken ct)
    {
        Session[] snapshot;
        lock (_gate)
            snapshot = _sessions.ToArray();
        foreach (var session in snapshot)
        {
            if (session.Socket.State != WebSocketState.Open)
                continue;
            try
            {
                await PushSessionAsync(session, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Не удалось отправить live-обновление");
            }
        }
    }

    async Task PushSessionAsync(Session session, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var payload = new Dictionary<string, object?>();
        if (session.Watch.HasFlag(LiveWatch.Tree))
        {
            var catalog = scope.ServiceProvider.GetRequiredService<Catalog>();
            payload["tree"] = await catalog.Tree(session.UserId, ct);
        }

        if (session.Watch.HasFlag(LiveWatch.Activity))
        {
            var admin = scope.ServiceProvider.GetRequiredService<AdminWork>();
            payload["activity"] = await admin.Activity(ct);
        }

        if (session.Watch.HasFlag(LiveWatch.Nodes))
        {
            var admin = scope.ServiceProvider.GetRequiredService<AdminWork>();
            payload["nodes"] = await admin.Nodes(ct);
        }

        var json = JsonSerializer.Serialize(payload, Json);
        var bytes = Encoding.UTF8.GetBytes(json);
        await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
