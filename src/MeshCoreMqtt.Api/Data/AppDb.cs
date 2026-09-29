using Microsoft.EntityFrameworkCore;

namespace MeshCoreMqtt.Api.Data;

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<BrokerNode> BrokerNodes => Set<BrokerNode>();
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<DeviceLogin> DeviceLogins => Set<DeviceLogin>();
    public DbSet<AccessGrant> Grants => Set<AccessGrant>();
    public DbSet<GrantTunnel> GrantTunnels => Set<GrantTunnel>();
    public DbSet<RoleTemplate> Roles => Set<RoleTemplate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserAccount>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
        });

        modelBuilder.Entity<Group>(e =>
        {
            e.ToTable("groups");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.OwnerUserId, x.Name }).IsUnique();
            e.HasOne(x => x.Owner).WithMany(x => x.Groups).HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BrokerNode>(e =>
        {
            e.ToTable("broker_nodes");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Host).HasMaxLength(255);
        });

        modelBuilder.Entity<Space>(e =>
        {
            e.ToTable("spaces");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.Group).WithMany(x => x.Spaces).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BrokerNode).WithMany(x => x.Spaces).HasForeignKey(x => x.BrokerNodeId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.BrokerNodeId);
            e.HasIndex(x => new { x.GroupId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<DeviceLogin>(e =>
        {
            e.ToTable("device_logins");
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Username).HasMaxLength(64);
            e.HasIndex(x => x.Username).IsUnique();
            e.HasOne(x => x.Space).WithMany(x => x.Devices).HasForeignKey(x => x.SpaceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccessGrant>(e =>
        {
            e.ToTable("access_grants");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.GroupId }).IsUnique();
        });

        modelBuilder.Entity<GrantTunnel>(e =>
        {
            e.ToTable("grant_tunnels");
            e.HasOne(x => x.Grant).WithMany(x => x.Tunnels).HasForeignKey(x => x.GrantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Space).WithMany().HasForeignKey(x => x.SpaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.GrantId, x.SpaceId }).IsUnique();
        });

        modelBuilder.Entity<RoleTemplate>(e =>
        {
            e.ToTable("roles");
            e.Property(x => x.Name).HasMaxLength(200);
        });
    }
}
