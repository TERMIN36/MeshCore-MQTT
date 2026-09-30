using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MeshCoreMqtt.Api.Data;
using MeshCoreMqtt.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MeshCoreMqtt.Api.Services;

public sealed class Catalog(AppDb db, Access access, Passwords passwords, DecisionCache cache, StatsStore stats, ProxyNotifier proxy, IOptions<AppSecrets> secrets)
{
    public async Task<object> Login(string? email, string? password, CancellationToken ct)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null || user.IsDisabled || password is null || !passwords.VerifyUser(user, password))
            throw new AppException(401, "Неверная почта или пароль");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secrets.Value.JwtKey));
        var token = new JwtSecurityToken(
            secrets.Value.JwtIssuer,
            secrets.Value.JwtIssuer,
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Role, user.IsAdmin ? "admin" : "user")
            ],
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new
        {
            token = new JwtSecurityTokenHandler().WriteToken(token),
            user = UserView(user)
        };
    }

    public async Task<IReadOnlyList<object>> Groups(Guid userId, CancellationToken ct)
    {
        var ids = await access.VisibleGroupIds(userId, ct);
        var groups = await db.Groups.AsNoTracking().Include(g => g.Owner).Where(g => ids.Contains(g.Id)).OrderBy(g => g.CreatedAt).ToListAsync(ct);
        var result = new List<object>();
        foreach (var group in groups)
        {
            var privileges = await access.OnGroup(userId, group, ct);
            var spaceCount = await VisibleSpaces(userId, group, ct).ContinueWith(t => t.Result.Count, ct);
            result.Add(new
            {
                id = group.Id,
                name = group.Name,
                owner = group.Owner.DisplayName,
                ownerEmail = group.Owner.Email,
                mine = group.OwnerUserId == userId,
                privileges = PrivilegeText.ToNames(privileges),
                spaceCount
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<object>> Tree(Guid userId, CancellationToken ct)
    {
        var ids = await access.VisibleGroupIds(userId, ct);
        if (ids.Count == 0)
            return [];

        var groups = await db.Groups.AsNoTracking().Include(g => g.Owner)
            .Where(g => ids.Contains(g.Id)).OrderBy(g => g.CreatedAt).ToListAsync(ct);
        var groupIds = groups.Select(g => g.Id).ToList();
        var spaces = await db.Spaces.AsNoTracking().Include(s => s.Devices)
            .Where(s => groupIds.Contains(s.GroupId)).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var grants = await db.Grants.AsNoTracking()
            .Include(g => g.User)
            .Include(g => g.Tunnels).ThenInclude(t => t.Space)
            .Where(g => groupIds.Contains(g.GroupId))
            .ToListAsync(ct);

        var result = new List<object>();
        foreach (var group in groups)
        {
            var mine = grants.Where(g => g.UserId == userId && g.GroupId == group.Id).SelectMany(g => g.Tunnels).ToList();
            var groupPrivileges = group.OwnerUserId == userId
                ? Privilege.All
                : mine.Aggregate(Privilege.None, (current, tunnel) => current | tunnel.Privileges);
            var groupSpaces = spaces.Where(s => s.GroupId == group.Id).ToList();
            var visible = groupSpaces.Where(space =>
                group.OwnerUserId == userId || mine.Any(t => t.SpaceId == space.Id && t.Privileges != Privilege.None)).ToList();
            if (groupPrivileges == Privilege.None && visible.Count == 0)
                continue;

            var showGroupGrants = group.OwnerUserId == userId || groupPrivileges.HasFlag(Privilege.Access);
            result.Add(new
            {
                id = group.Id,
                name = group.Name,
                owner = group.Owner.DisplayName,
                ownerEmail = group.Owner.Email,
                createdAt = group.CreatedAt,
                mine = group.OwnerUserId == userId,
                privileges = PrivilegeText.ToNames(groupPrivileges),
                spaces = await BuildSpaces(group, userId, mine, visible, ct),
                spaceChoices = showGroupGrants
                    ? groupSpaces.Select(space => (object)new
                    {
                        id = space.Id,
                        name = space.Name,
                        privileges = PrivilegeText.ToNames(PrivilegesOn(group, userId, mine, space.Id))
                    })
                    : Array.Empty<object>(),
                grants = showGroupGrants
                    ? grants.Where(g => g.GroupId == group.Id).Select(GrantView)
                    : Array.Empty<object>()
            });
        }

        return result;
    }

    async Task<IReadOnlyList<object>> BuildSpaces(Group group, Guid userId, List<GrantTunnel> mine, List<Space> visible, CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var space in visible)
        {
            var privileges = PrivilegesOn(group, userId, mine, space.Id);
            var heard = privileges.HasFlag(Privilege.View) ? NodesFor(space) : [];
            var devices = privileges.HasFlag(Privilege.Credentials)
                ? await DeviceViews(space.Devices, heard, ct)
                : Array.Empty<object>();
            rows.Add(new
            {
                id = space.Id,
                name = space.Name,
                privileges = PrivilegeText.ToNames(privileges),
                devices,
                activity = privileges.HasFlag(Privilege.View) ? ActivityFor(space) : Array.Empty<object>(),
                feed = privileges.HasFlag(Privilege.View) ? FeedFor(space) : Array.Empty<object>(),
                nodes = heard.Select(LiveObject)
            });
        }

        return rows;
    }

    public async Task<object> CreateGroup(Guid userId, string? name, CancellationToken ct)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = RequireName(name),
            OwnerUserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        await EnsureUniqueGroupName(userId, null, group.Name, ct);
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return new { id = group.Id, name = group.Name };
    }

    public async Task<object> GroupDetails(Guid userId, Guid groupId, CancellationToken ct)
    {
        var group = await LoadGroup(groupId, ct);
        var privileges = await access.OnGroup(userId, group, ct);
        var spaces = await VisibleSpaces(userId, group, ct);
        if (privileges == Privilege.None && spaces.Count == 0)
            throw new AppException(404, "Группа не найдена");

        var grants = await db.Grants.AsNoTracking().Include(g => g.User).Include(g => g.Tunnels).ThenInclude(t => t.Space)
            .Where(g => g.GroupId == groupId).ToListAsync(ct);
        return new
        {
            id = group.Id,
            name = group.Name,
            owner = group.Owner.DisplayName,
            ownerEmail = group.Owner.Email,
            mine = group.OwnerUserId == userId,
            privileges = PrivilegeText.ToNames(privileges),
            spaces = spaces.Select(s => new { id = s.Id, name = s.Name, deviceCount = s.Devices.Count }),
            grants = privileges.HasFlag(Privilege.Access) || group.OwnerUserId == userId
                ? grants.Select(GrantView)
                : Array.Empty<object>()
        };
    }

    public async Task RenameGroup(Guid userId, Guid groupId, string? name, CancellationToken ct)
    {
        var group = await LoadGroup(groupId, ct);
        access.Require(await access.OnGroup(userId, group, ct), Privilege.Access);
        group.Name = RequireName(name);
        await EnsureUniqueGroupName(group.OwnerUserId, group.Id, group.Name, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteGroup(Guid userId, Guid groupId, CancellationToken ct)
    {
        var group = await LoadGroup(groupId, ct);
        if (group.OwnerUserId != userId)
            throw new AppException(403, "Удалить группу может только владелец");
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
    }

    public async Task<object> CreateSpace(Guid userId, Guid groupId, string? name, CancellationToken ct)
    {
        var group = await LoadGroup(groupId, ct);
        access.Require(await access.OnGroup(userId, group, ct), Privilege.Access);
        var node = await PickNode(ct);
        var space = new Space
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            Name = RequireName(name),
            BrokerNodeId = node.Id,
            CreatedAt = DateTime.UtcNow
        };
        await EnsureUniqueSpaceName(groupId, null, space.Name, ct);
        db.Spaces.Add(space);
        await db.SaveChangesAsync(ct);
        return new { id = space.Id, name = space.Name };
    }

    public async Task<object> SpaceDetails(Guid userId, Guid spaceId, CancellationToken ct)
    {
        var space = await LoadSpace(spaceId, ct);
        var privileges = await access.OnSpace(userId, space, ct);
        if (privileges == Privilege.None)
            throw new AppException(404, "Тунель не найден");

        return new
        {
            id = space.Id,
            name = space.Name,
            groupId = space.GroupId,
            groupName = space.Group.Name,
            privileges = PrivilegeText.ToNames(privileges),
            connection = new { host = secrets.Value.PublicHost, port = secrets.Value.PublicPort, tls = true },
            devices = privileges.HasFlag(Privilege.Credentials)
                ? await DeviceViews(space.Devices, privileges.HasFlag(Privilege.View) ? NodesFor(space) : [], ct)
                : Array.Empty<object>(),
            activity = privileges.HasFlag(Privilege.View) ? ActivityFor(space) : Array.Empty<object>(),
            feed = privileges.HasFlag(Privilege.View) ? FeedFor(space) : Array.Empty<object>(),
            nodes = privileges.HasFlag(Privilege.View) ? NodesFor(space).Select(LiveObject) : Array.Empty<object>()
        };
    }

    public async Task RenameSpace(Guid userId, Guid spaceId, string? name, CancellationToken ct)
    {
        var space = await LoadSpace(spaceId, ct);
        access.Require(await access.OnSpace(userId, space, ct), Privilege.Access);
        space.Name = RequireName(name);
        await EnsureUniqueSpaceName(space.GroupId, space.Id, space.Name, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSpace(Guid userId, Guid spaceId, CancellationToken ct)
    {
        var space = await LoadSpace(spaceId, ct);
        access.Require(await access.OnSpace(userId, space, ct), Privilege.Access);
        var usernames = space.Devices.Select(d => d.Username).ToList();
        db.Spaces.Remove(space);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await proxy.Drop(usernames, ct);
    }

    public async Task<object> CreateDevice(Guid userId, Guid spaceId, string? name, bool canSubscribe, bool canPublish, CancellationToken ct)
    {
        var space = await LoadSpace(spaceId, ct);
        var privileges = await access.OnSpace(userId, space, ct);
        access.Require(privileges, Privilege.Credentials);
        if (!canSubscribe && !canPublish)
            throw new AppException(400, "Клиенту нужна подписка или публикация");
        if (canSubscribe)
            access.Require(privileges, Privilege.Subscribe);
        if (canPublish)
            access.Require(privileges, Privilege.Publish);

        var device = new DeviceLogin
        {
            Id = Guid.NewGuid(),
            SpaceId = space.Id,
            DisplayName = OptionalName(name),
            Username = SecretText.Username(),
            CanSubscribe = canSubscribe,
            CanPublish = canPublish,
            CreatedAt = DateTime.UtcNow
        };
        var password = SecretText.Password();
        device.PasswordHash = passwords.HashDevice(device, password);
        string certificate;
        try
        {
            certificate = CertificateFiles.FirstCertificate(
                await File.ReadAllTextAsync(Path.Combine(secrets.Value.CertsDirectory, "ca.crt"), ct));
        }
        catch (CertificateException ex)
        {
            throw new AppException(500, ex.Message);
        }
        db.DeviceLogins.Add(device);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        return new
        {
            id = device.Id,
            name = DeviceTitle(device, null),
            config = RepeaterConfig.Encode(
                secrets.Value.PublicHost,
                secrets.Value.PublicPort,
                device.Username,
                password,
                certificate)
        };
    }

    public async Task DeleteDevice(Guid userId, Guid deviceId, CancellationToken ct)
    {
        var device = await db.DeviceLogins.Include(d => d.Space).ThenInclude(s => s.Group).FirstOrDefaultAsync(d => d.Id == deviceId, ct)
            ?? throw new AppException(404, "Клиент не найден");
        access.Require(await access.OnSpace(userId, device.Space, ct), Privilege.Credentials);
        db.DeviceLogins.Remove(device);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
        await proxy.Drop([device.Username], ct);
    }

    public async Task<object> Grant(Guid actorId, Guid groupId, string? email, CancellationToken ct)
    {
        var group = await LoadGroup(groupId, ct);
        access.Require(await access.OnGroup(actorId, group, ct), Privilege.Access);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == (email ?? "").Trim().ToLowerInvariant(), ct)
            ?? throw new AppException(404, "Пользователь не найден");
        if (user.IsDisabled)
            throw new AppException(400, "Учётка отключена");
        if (user.Id == group.OwnerUserId)
            throw new AppException(400, "Владелец уже имеет доступ ко всем тунелям");

        var existing = await db.Grants.FirstOrDefaultAsync(g => g.UserId == user.Id && g.GroupId == groupId, ct);
        if (existing is null)
        {
            existing = new AccessGrant
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                GroupId = groupId,
                CreatedAt = DateTime.UtcNow
            };
            db.Grants.Add(existing);
            await db.SaveChangesAsync(ct);
            cache.Invalidate();
        }

        return new { id = existing.Id };
    }

    public async Task SetTunnels(Guid actorId, Guid grantId, IReadOnlyList<TunnelMark> marks, CancellationToken ct)
    {
        var grant = await db.Grants.Include(g => g.Group).Include(g => g.Tunnels)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct) ?? throw new AppException(404, "Доступ не найден");
        access.Require(await access.OnGroup(actorId, grant.Group, ct), Privilege.Access);

        var spaces = await db.Spaces.Include(s => s.Group).Where(s => s.GroupId == grant.GroupId).ToListAsync(ct);
        var known = spaces.Select(s => s.Id).ToHashSet();
        var desired = new Dictionary<Guid, Privilege>();
        foreach (var mark in marks)
        {
            if (!known.Contains(mark.SpaceId))
                throw new AppException(400, "Тунель не из этой группы");
            if (!desired.TryAdd(mark.SpaceId, PrivilegeText.Parse(mark.Privileges)))
                throw new AppException(400, "Тунель указан дважды");
        }

        foreach (var space in spaces)
        {
            var actorOnSpace = await access.OnSpace(actorId, space, ct);
            desired.TryGetValue(space.Id, out var next);
            var existing = grant.Tunnels.FirstOrDefault(t => t.SpaceId == space.Id);
            if (!actorOnSpace.HasFlag(Privilege.Access))
            {
                if (next != Privilege.None)
                    throw new AppException(403, "Нельзя выдать права шире своих");
                continue;
            }

            if (existing is not null && existing.Privileges == next)
                continue;
            if ((next & ~actorOnSpace) != Privilege.None)
                throw new AppException(403, "Нельзя выдать права шире своих");
            if (next == Privilege.None)
            {
                if (existing is not null)
                    db.GrantTunnels.Remove(existing);
                continue;
            }

            if (existing is null)
            {
                db.GrantTunnels.Add(new GrantTunnel
                {
                    Id = Guid.NewGuid(),
                    GrantId = grant.Id,
                    SpaceId = space.Id,
                    Privileges = next
                });
            }
            else
            {
                existing.Privileges = next;
            }
        }

        await db.SaveChangesAsync(ct);
        cache.Invalidate();
    }

    public async Task Revoke(Guid actorId, Guid grantId, CancellationToken ct)
    {
        var grant = await db.Grants.Include(g => g.Group).Include(g => g.Tunnels).ThenInclude(t => t.Space).ThenInclude(s => s.Group)
            .FirstOrDefaultAsync(g => g.Id == grantId, ct) ?? throw new AppException(404, "Доступ не найден");
        access.Require(await access.OnGroup(actorId, grant.Group, ct), Privilege.Access);
        foreach (var tunnel in grant.Tunnels)
            access.Require(await access.OnSpace(actorId, tunnel.Space, ct), Privilege.Access);

        db.Grants.Remove(grant);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();
    }

    public async Task<IReadOnlyList<object>> Roles(Guid userId, CancellationToken ct) =>
        await db.Roles.AsNoTracking()
            .Where(r => r.IsSystem || r.OwnerUserId == userId)
            .OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name)
            .Select(r => (object)new { id = r.Id, name = r.Name, system = r.IsSystem, privileges = PrivilegeText.ToNames(r.Privileges) })
            .ToListAsync(ct);

    public async Task<object> CreateRole(Guid userId, string? name, IEnumerable<string>? privilegeNames, CancellationToken ct)
    {
        var role = new RoleTemplate
        {
            Id = Guid.NewGuid(),
            Name = RequireName(name),
            OwnerUserId = userId,
            Privileges = PrivilegeText.Parse(privilegeNames)
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return new { id = role.Id };
    }

    public object Connection() => new
    {
        host = secrets.Value.PublicHost,
        port = secrets.Value.PublicPort,
        tls = true
    };

    async Task<BrokerNode> PickNode(CancellationToken ct)
    {
        var nodes = await db.BrokerNodes.Where(n => n.Status == NodeStatus.Open).ToListAsync(ct);
        if (nodes.Count == 0)
            throw new AppException(409, "Нет узла, который принимает новые тунели");
        var counts = await db.Spaces.GroupBy(s => s.BrokerNodeId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return nodes
            .OrderBy(n => stats.MessagesPerMinute(n.Id))
            .ThenBy(n => counts.GetValueOrDefault(n.Id))
            .ThenBy(n => n.CreatedAt)
            .First();
    }

    async Task<List<Space>> VisibleSpaces(Guid userId, Group group, CancellationToken ct)
    {
        var spaces = await db.Spaces.AsNoTracking().Include(s => s.Devices)
            .Where(s => s.GroupId == group.Id).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        if (group.OwnerUserId == userId)
            return spaces;
        var allowed = await db.GrantTunnels.AsNoTracking()
            .Where(t => t.Grant.UserId == userId && t.Grant.GroupId == group.Id && t.Privileges != Privilege.None)
            .Select(t => t.SpaceId)
            .ToListAsync(ct);
        return spaces.Where(s => allowed.Contains(s.Id)).ToList();
    }

    IEnumerable<object> ActivityFor(Space space)
    {
        var filter = MeshTopics.Filter(space.Id);
        return stats.Topics()
            .Where(row => row.NodeId == space.BrokerNodeId && TopicRules.AllowsPublish([filter], row.Topic))
            .OrderByDescending(row => row.MessagesPerMinute)
            .Select(row => new { topic = MeshTopics.Tail(space.Id, row.Topic), messagesPerMinute = row.MessagesPerMinute, lastSeen = row.LastSeen });
    }

    IEnumerable<object> FeedFor(Space space)
    {
        var filter = MeshTopics.Filter(space.Id);
        return stats.Feed()
            .Where(row => row.NodeId == space.BrokerNodeId && TopicRules.AllowsPublish([filter], row.Topic))
            .OrderByDescending(row => row.Seen)
            .Take(80)
            .Select(row => new
            {
                topic = MeshTopics.Tail(space.Id, row.Topic),
                seen = row.Seen,
                publicKey = row.PublicKey,
                name = row.Name,
                summary = row.Summary
            });
    }

    List<RepeaterLive> NodesFor(Space space)
    {
        var filter = MeshTopics.Filter(space.Id);
        return stats.Repeaters()
            .Where(row => row.NodeId == space.BrokerNodeId && TopicRules.AllowsPublish([filter], row.Topic))
            .OrderByDescending(row => row.LastSeen)
            .ToList();
    }

    async Task<IReadOnlyList<object>> DeviceViews(IEnumerable<DeviceLogin> devices, IReadOnlyList<RepeaterLive> heard, CancellationToken ct)
    {
        var ordered = devices.OrderBy(d => d.CreatedAt).ToList();
        var matched = MatchDevices(ordered, heard);
        var dirty = false;
        foreach (var device in ordered)
        {
            if (!matched.TryGetValue(device.Id, out var live))
                continue;
            var learned = LiveName(live);
            if (learned is null || device.DisplayName == learned)
                continue;
            device.DisplayName = learned;
            await db.DeviceLogins
                .Where(row => row.Id == device.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.DisplayName, learned), ct);
            dirty = true;
        }

        if (dirty)
            cache.Invalidate();

        return ordered.Select(device =>
        {
            matched.TryGetValue(device.Id, out var live);
            return DeviceView(device, live);
        }).ToList();
    }

    static object DeviceView(DeviceLogin device, RepeaterLive? live) => new
    {
        id = device.Id,
        name = DeviceTitle(device, live),
        canSubscribe = device.CanSubscribe,
        canPublish = device.CanPublish,
        createdAt = device.CreatedAt,
        live = live is null ? null : LiveObject(live)
    };

    static Dictionary<Guid, RepeaterLive?> MatchDevices(IReadOnlyList<DeviceLogin> devices, IReadOnlyList<RepeaterLive> heard)
    {
        var result = devices.ToDictionary(device => device.Id, _ => (RepeaterLive?)null);
        var remaining = heard.ToList();
        foreach (var device in devices.Where(item => item.DisplayName.Length > 0))
        {
            var named = remaining
                .Where(node => NamesMatch(device.DisplayName, node))
                .OrderByDescending(node => node.LastSeen)
                .FirstOrDefault();
            if (named is null)
                continue;
            result[device.Id] = named;
            remaining.Remove(named);
        }

        var unmatched = devices.Where(device => result[device.Id] is null).ToList();
        // Приветствие и пульс шлёт сам репитер. Остальные ключи — соседи, которых он уже слышал.
        var announced = remaining.Where(Announced).ToList();
        if (unmatched.Count == 1 && announced.Count == 1)
            result[unmatched[0].Id] = announced[0];
        else if (unmatched.Count == 1 && remaining.Count == 1)
            result[unmatched[0].Id] = remaining[0];
        return result;
    }

    static bool Announced(RepeaterLive node) =>
        node.FrequencyHz is not null || node.BandwidthHz is not null || node.SpreadingFactor is not null ||
        node.Forwarding is not null || node.PacketsPublished is not null || node.PacketsInbound is not null;

    static bool NamesMatch(string name, RepeaterLive node) =>
        (node.Name.Length > 0 && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) ||
        (node.AdvertName is { Length: > 0 } advert && string.Equals(advert, name, StringComparison.OrdinalIgnoreCase));

    static string DeviceTitle(DeviceLogin device, RepeaterLive? live) =>
        LiveName(live) ?? (device.DisplayName.Length > 0 ? device.DisplayName : "Репитер");

    static string? LiveName(RepeaterLive? live)
    {
        if (live is null)
            return null;
        if (live.AdvertName is { Length: > 0 } advert)
            return advert;
        return live.Name.Length > 0 ? live.Name : null;
    }

    static object LiveObject(RepeaterLive live) => new
    {
        publicKey = live.PublicKey,
        name = live.Name,
        lastSeen = live.LastSeen,
        clock = live.Clock,
        latitude = live.Latitude,
        longitude = live.Longitude,
        locationFromAdvert = live.LocationFromAdvert,
        advertType = live.AdvertType,
        advertName = live.AdvertName,
        advertAt = live.AdvertAt,
        frequencyHz = live.FrequencyHz,
        bandwidthHz = live.BandwidthHz,
        spreadingFactor = live.SpreadingFactor,
        codingRate = live.CodingRate,
        txDbm = live.TxDbm,
        antennaCm = live.AntennaCm,
        forwarding = live.Forwarding,
        packetsPublished = live.PacketsPublished,
        packetsInbound = live.PacketsInbound,
        duplicates = live.Duplicates,
        publishErrors = live.PublishErrors,
        noiseFloor = live.NoiseFloor,
        txAirSecs = live.TxAirSecs,
        rxAirSecs = live.RxAirSecs,
        uptimeSecs = live.UptimeSecs,
        txQueue = live.TxQueue,
        batteryMv = live.BatteryMv,
        tempCx10 = live.TempCx10,
        firmware = live.Firmware
    };

    async Task<Group> LoadGroup(Guid id, CancellationToken ct) =>
        await db.Groups.Include(g => g.Owner).FirstOrDefaultAsync(g => g.Id == id, ct)
        ?? throw new AppException(404, "Группа не найдена");

    async Task<Space> LoadSpace(Guid id, CancellationToken ct) =>
        await db.Spaces.Include(s => s.Group).ThenInclude(g => g.Owner)
            .Include(s => s.Devices).Include(s => s.BrokerNode)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw new AppException(404, "Тунель не найден");

    static object UserView(UserAccount user) => new
    {
        id = user.Id,
        email = user.Email,
        name = user.DisplayName,
        admin = user.IsAdmin
    };

    static Privilege PrivilegesOn(Group group, Guid userId, IEnumerable<GrantTunnel> mine, Guid spaceId)
    {
        if (group.OwnerUserId == userId)
            return Privilege.All;
        return mine.Where(t => t.SpaceId == spaceId).Aggregate(Privilege.None, (current, tunnel) => current | tunnel.Privileges);
    }

    static object GrantView(AccessGrant grant) => new
    {
        id = grant.Id,
        email = grant.User.Email,
        name = grant.User.DisplayName,
        tunnels = grant.Tunnels
            .OrderBy(t => t.Space.Name)
            .Select(t => new
            {
                spaceId = t.SpaceId,
                name = t.Space.Name,
                privileges = PrivilegeText.ToNames(t.Privileges)
            })
    };

    async Task EnsureUniqueGroupName(Guid ownerId, Guid? exceptGroupId, string name, CancellationToken ct)
    {
        var key = name.ToLowerInvariant();
        var query = db.Groups.Where(g => g.OwnerUserId == ownerId && g.Name.ToLower() == key);
        if (exceptGroupId is Guid groupId)
            query = query.Where(g => g.Id != groupId);
        if (await query.AnyAsync(ct))
            throw new AppException(409, "У вас уже есть группа с таким именем");
    }

    async Task EnsureUniqueSpaceName(Guid groupId, Guid? exceptSpaceId, string name, CancellationToken ct)
    {
        var key = name.ToLowerInvariant();
        var query = db.Spaces.Where(s => s.GroupId == groupId && s.Name.ToLower() == key);
        if (exceptSpaceId is Guid spaceId)
            query = query.Where(s => s.Id != spaceId);
        if (await query.AnyAsync(ct))
            throw new AppException(409, "В этой группе уже есть тунель с таким именем");
    }

    static string RequireName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is < 1 or > 200)
            throw new AppException(400, "Имя должно быть от 1 до 200 символов");
        return trimmed;
    }

    static string OptionalName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            return "";
        if (trimmed.Length > 200)
            throw new AppException(400, "Имя должно быть от 1 до 200 символов");
        return trimmed;
    }
}

public sealed record TunnelMark(Guid SpaceId, string[]? Privileges);
