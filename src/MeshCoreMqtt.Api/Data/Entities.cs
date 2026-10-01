namespace MeshCoreMqtt.Api.Data;

[Flags]
public enum Privilege
{
    None = 0,
    View = 1,
    Subscribe = 2,
    Publish = 4,
    Credentials = 8,
    Topics = 16,
    Access = 32,
    All = View | Subscribe | Publish | Credentials | Topics | Access
}

public enum NodeStatus
{
    Open = 0,
    Draining = 1,
    Offline = 2
}

public sealed class UserAccount
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool IsDisabled { get; set; }
    public bool CanCreateGroups { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Group> Groups { get; set; } = [];
}

public sealed class Group
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid OwnerUserId { get; set; }
    public UserAccount Owner { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public List<Space> Spaces { get; set; } = [];
}

public sealed class BrokerNode
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool UseTls { get; set; }
    public NodeStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Space> Spaces { get; set; } = [];
}

public sealed class Space
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public string Name { get; set; } = "";
    public Guid BrokerNodeId { get; set; }
    public BrokerNode BrokerNode { get; set; } = null!;
    public bool IsMoving { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<DeviceLogin> Devices { get; set; } = [];
}

public sealed class DeviceLogin
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Space Space { get; set; } = null!;
    public string DisplayName { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool CanSubscribe { get; set; }
    public bool CanPublish { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AccessGrant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public UserAccount User { get; set; } = null!;
    public Guid GroupId { get; set; }
    public Group Group { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public List<GrantTunnel> Tunnels { get; set; } = [];
}

public sealed class GrantTunnel
{
    public Guid Id { get; set; }
    public Guid GrantId { get; set; }
    public AccessGrant Grant { get; set; } = null!;
    public Guid SpaceId { get; set; }
    public Space Space { get; set; } = null!;
    public Privilege Privileges { get; set; }
}

public sealed class RoleTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? OwnerUserId { get; set; }
    public Privilege Privileges { get; set; }
    public bool IsSystem { get; set; }
}
