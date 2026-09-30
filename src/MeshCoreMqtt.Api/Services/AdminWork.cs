using MeshCoreMqtt.Api.Data;
using MeshCoreMqtt.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeshCoreMqtt.Api.Services;

public sealed class AdminWork(AppDb db, Passwords passwords, DecisionCache cache, StatsStore stats, ProxyNotifier proxy, IOptions<AppSecrets> secrets)
{
    public async Task<IReadOnlyList<object>> Users(CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt)
            .Select(u => (object)new
            {
                id = u.Id,
                email = u.Email,
                name = u.DisplayName,
                admin = u.IsAdmin,
                disabled = u.IsDisabled,
                createdAt = u.CreatedAt
            }).ToListAsync(ct);

    public async Task<object> CreateUser(string? email, string? name, string? password, bool admin, CancellationToken ct)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        if (normalized.Length < 3 || !normalized.Contains('@'))
            throw new AppException(400, "Нужна почта");
        if ((password ?? "").Length < 8)
            throw new AppException(400, "Пароль панели должен быть не короче 8 символов");
        if (await db.Users.AnyAsync(u => u.Email == normalized, ct))
            throw new AppException(409, "Такая почта уже есть");

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            DisplayName = (name ?? "").Trim() is { Length: > 0 } display ? display : normalized,
            IsAdmin = admin,
            CreatedAt = DateTime.UtcNow
        };
        if (user.DisplayName.Length > 200)
            throw new AppException(400, "Имя слишком длинное");
        user.PasswordHash = passwords.HashUser(user, password!);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return new { id = user.Id };
    }

    public async Task UpdateUser(Guid userId, bool? disabled, bool? admin, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new AppException(404, "Учётка не найдена");
        if (disabled is bool off)
            user.IsDisabled = off;
        if (admin is bool isAdmin)
            user.IsAdmin = isAdmin;
        if (user.IsAdmin && user.IsDisabled)
            throw new AppException(400, "Сначала снимите права администратора или не отключайте эту учётку");
        var admins = await db.Users.CountAsync(u => u.IsAdmin && !u.IsDisabled && u.Id != user.Id, ct);
        if (user.IsAdmin && !user.IsDisabled)
            admins++;
        if (admins == 0)
            throw new AppException(400, "Должен остаться хотя бы один администратор");
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> UserTree(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new AppException(404, "Учётка не найдена");
        var owned = await db.Groups.AsNoTracking()
            .Include(g => g.Spaces).ThenInclude(s => s.BrokerNode)
            .Where(g => g.OwnerUserId == userId).ToListAsync(ct);
        var grants = await db.Grants.AsNoTracking().Include(g => g.Group).Include(g => g.Tunnels).ThenInclude(t => t.Space)
            .Where(g => g.UserId == userId).ToListAsync(ct);
        return new
        {
            id = user.Id,
            email = user.Email,
            name = user.DisplayName,
            admin = user.IsAdmin,
            disabled = user.IsDisabled,
            groups = owned.Select(g => new
            {
                id = g.Id,
                name = g.Name,
                spaces = g.Spaces.Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    node = s.BrokerNode.Name
                })
            }),
            grants = grants.Select(g => new
            {
                id = g.Id,
                group = g.Group.Name,
                tunnels = g.Tunnels.OrderBy(t => t.Space.Name).Select(t => new
                {
                    name = t.Space.Name,
                    privileges = PrivilegeText.ToNames(t.Privileges)
                })
            })
        };
    }

    public async Task<IReadOnlyList<object>> Sharing(CancellationToken ct)
    {
        var grants = await db.Grants.AsNoTracking()
            .Include(g => g.User)
            .Include(g => g.Group)
            .Include(g => g.Tunnels).ThenInclude(t => t.Space)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync(ct);
        return grants.Select(g => (object)new
        {
            id = g.Id,
            email = g.User.Email,
            name = g.User.DisplayName,
            group = g.Group.Name,
            tunnels = g.Tunnels.OrderBy(t => t.Space.Name).Select(t => new
            {
                name = t.Space.Name,
                privileges = PrivilegeText.ToNames(t.Privileges)
            })
        }).ToList();
    }

    public async Task<IReadOnlyList<object>> Activity(CancellationToken ct)
    {
        var spaces = await db.Spaces.AsNoTracking().Include(s => s.Group).Include(s => s.BrokerNode).ToListAsync(ct);
        return stats.Topics()
            .Where(row => !row.Topic.StartsWith('$'))
            .OrderByDescending(row => row.MessagesPerMinute)
            .Select(row =>
            {
                var matches = spaces.Where(s => s.BrokerNodeId == row.NodeId && TopicRules.AllowsPublish([MeshTopics.Filter(s.Id)], row.Topic)).ToList();
                var topic = matches.Count == 1 ? MeshTopics.Tail(matches[0].Id, row.Topic) : row.Topic;
                return (object)new
                {
                    topic,
                    messagesPerMinute = row.MessagesPerMinute,
                    lastSeen = row.LastSeen,
                    node = matches.FirstOrDefault()?.BrokerNode.Name,
                    spaces = matches.Select(s => new { id = s.Id, name = s.Name, group = s.Group.Name })
                };
            }).ToList();
    }

    public async Task<IReadOnlyList<object>> Nodes(CancellationToken ct)
    {
        var nodes = await db.BrokerNodes.AsNoTracking().Include(n => n.Spaces).ThenInclude(s => s.Group).OrderBy(n => n.CreatedAt).ToListAsync(ct);
        return nodes.Select(n =>
        {
            var runtime = stats.NodeView(n.Id);
            return (object)new
            {
                id = n.Id,
                name = n.Name,
                host = n.Host,
                port = n.Port,
                tls = n.UseTls,
                status = StatusName(n.Status),
                spaceCount = n.Spaces.Count,
                spaces = n.Spaces.Select(s => new { id = s.Id, name = s.Name, group = s.Group.Name, moving = s.IsMoving }),
                runtime
            };
        }).ToList();
    }

    public async Task<object> AddNode(string? name, string? host, int port, bool tls, CancellationToken ct)
    {
        var trimmedHost = (host ?? "").Trim();
        if (trimmedHost.Length is < 1 or > 255)
            throw new AppException(400, "Нужен адрес узла");
        if (port is < 1 or > 65535)
            throw new AppException(400, "Некорректный порт");
        if (!await StatsCollector.Probe(trimmedHost, port, ct))
            throw new AppException(400, "Брокер не отвечает");

        var node = new BrokerNode
        {
            Id = Guid.NewGuid(),
            Name = (name ?? "").Trim() is { Length: > 0 } display ? display : trimmedHost,
            Host = trimmedHost,
            Port = port,
            UseTls = tls,
            Status = NodeStatus.Open,
            CreatedAt = DateTime.UtcNow
        };
        db.BrokerNodes.Add(node);
        await db.SaveChangesAsync(ct);
        return new { id = node.Id };
    }

    public async Task SetStatus(Guid nodeId, string? status, CancellationToken ct)
    {
        var node = await db.BrokerNodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new AppException(404, "Узел не найден");
        node.Status = (status ?? "").ToLowerInvariant() switch
        {
            "open" => NodeStatus.Open,
            "draining" => NodeStatus.Draining,
            "offline" => NodeStatus.Offline,
            _ => throw new AppException(400, "Статус: open, draining или offline")
        };
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteNode(Guid nodeId, CancellationToken ct)
    {
        var node = await db.BrokerNodes.Include(n => n.Spaces).FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new AppException(404, "Узел не найден");
        if (node.Spaces.Count > 0)
            throw new AppException(409, "На узле ещё есть тунели");
        if (await db.BrokerNodes.CountAsync(ct) == 1)
            throw new AppException(400, "Нельзя удалить последний узел");
        db.BrokerNodes.Remove(node);
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveSpace(Guid spaceId, Guid targetNodeId, CancellationToken ct)
    {
        var space = await db.Spaces.Include(s => s.Devices).FirstOrDefaultAsync(s => s.Id == spaceId, ct)
            ?? throw new AppException(404, "Тунель не найден");
        if (space.IsMoving)
            throw new AppException(409, "Перенос этого тунеля уже идёт");
        var target = await db.BrokerNodes.FirstOrDefaultAsync(n => n.Id == targetNodeId, ct)
            ?? throw new AppException(404, "Узел не найден");
        if (target.Id == space.BrokerNodeId)
            throw new AppException(400, "Тунель уже на этом узле");
        if (target.Status == NodeStatus.Offline)
            throw new AppException(400, "Нельзя переносить на выключенный узел");
        if (!await StatsCollector.Probe(target.Host, target.Port, ct))
            throw new AppException(400, "Узел назначения не отвечает");

        space.IsMoving = true;
        space.BrokerNodeId = target.Id;
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        try
        {
            await proxy.Drop(space.Devices.Select(d => d.Username).ToList(), ct);
        }
        finally
        {
            space.IsMoving = false;
            await db.SaveChangesAsync(ct);
        }
    }

    public object Certificate()
    {
        try
        {
            var directory = secrets.Value.CertsDirectory;
            var info = CertificateFiles.Describe(directory);
            var hosts = CertificateFiles.ConfiguredHosts(directory);
            if (hosts.Count == 0)
                hosts = [info.Host];
            return new { host = info.Host, notAfter = info.NotAfter, names = info.Names, hosts };
        }
        catch (CertificateException ex)
        {
            throw new AppException(404, ex.Message);
        }
    }

    public async Task<object> ReissueCertificate(string? host, CancellationToken ct)
    {
        try
        {
            secrets.Value.PublicHost = CertificateFiles.Reissue(secrets.Value.CertsDirectory, host);
        }
        catch (CertificateException ex)
        {
            throw new AppException(400, ex.Message);
        }
        await proxy.ReloadCertificate(ct);
        return Certificate();
    }

    public string ExportKey()
    {
        try
        {
            return CertificateFiles.ExportServerKey(secrets.Value.CertsDirectory);
        }
        catch (CertificateException ex)
        {
            throw new AppException(404, ex.Message);
        }
    }

    public async Task<object> ImportKey(string? pem, CancellationToken ct)
    {
        try
        {
            CertificateFiles.ImportServerKey(secrets.Value.CertsDirectory, pem);
        }
        catch (CertificateException ex)
        {
            throw new AppException(400, ex.Message);
        }

        if (CertificateFiles.ReadHost(secrets.Value.CertsDirectory) is { } host)
            secrets.Value.PublicHost = host;
        await proxy.ReloadCertificate(ct);
        return Certificate();
    }

    static string StatusName(NodeStatus status) => status switch
    {
        NodeStatus.Open => "open",
        NodeStatus.Draining => "draining",
        _ => "offline"
    };
}
