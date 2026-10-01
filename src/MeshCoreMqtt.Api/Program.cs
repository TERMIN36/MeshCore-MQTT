using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeshCoreMqtt.Api.Data;
using MeshCoreMqtt.Api.Services;
using MeshCoreMqtt.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var secrets = new AppSecrets
{
    JwtKey = builder.Configuration["Jwt:Key"] ?? "",
    JwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "meshcore",
    InternalToken = builder.Configuration["Internal:Token"] ?? "",
    AdminEmail = builder.Configuration["Admin:Email"] ?? "",
    AdminPassword = builder.Configuration["Admin:Password"] ?? "",
    Superuser = builder.Configuration["Mqtt:Superuser"] ?? "stats",
    SuperuserPassword = builder.Configuration["Mqtt:SuperuserPassword"] ?? "",
    PublicHost = builder.Configuration["Mqtt:PublicHost"] ?? "localhost",
    PublicPort = int.TryParse(builder.Configuration["Mqtt:PublicPort"], out var port) ? port : 8883,
    CertsDirectory = builder.Configuration["Certs:Directory"] ?? "certs",
    ProxyDropUrl = builder.Configuration["Proxy:DropUrl"] ?? "",
    ProxyReloadUrl = builder.Configuration["Proxy:ReloadUrl"] ?? ""
};
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(secrets));
builder.Services.AddDbContext<AppDb>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<Passwords>();
builder.Services.AddSingleton<DecisionCache>();
builder.Services.AddSingleton<LiveHub>();
builder.Services.AddSingleton<StatsStore>();
builder.Services.AddScoped<Access>();
builder.Services.AddScoped<MqttGate>();
builder.Services.AddScoped<Catalog>();
builder.Services.AddScoped<AdminWork>();
builder.Services.AddHttpClient<ProxyNotifier>();
builder.Services.AddHostedService<StatsCollector>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = secrets.JwtIssuer,
        ValidAudience = secrets.JwtIssuer,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secrets.JwtKey))
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token))
                context.Token = token;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
await InitializeAsync(app, secrets);

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var status = error is AppException appError ? appError.Status : 500;
    var message = error is AppException known ? known.Message : "Внутренняя ошибка";
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error = message });
}));
app.UseCors();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

var brokerApi = app.MapGroup("/internal/mqtt");
brokerApi.MapPost("/auth", async (HttpRequest request, MqttGate gate, CancellationToken ct) =>
{
    var body = await ReadBody(request, ct);
    var ok = await gate.Authenticate(Field(body, "username"), Field(body, "password"), ct);
    return ok ? Results.Ok() : Results.Unauthorized();
});
brokerApi.MapPost("/superuser", async (HttpRequest request, MqttGate gate, CancellationToken ct) =>
{
    var body = await ReadBody(request, ct);
    return await gate.IsSuperuser(Field(body, "username")) ? Results.Ok() : Results.Unauthorized();
});
brokerApi.MapPost("/acl", async (HttpRequest request, MqttGate gate, CancellationToken ct) =>
{
    var body = await ReadBody(request, ct);
    var acc = 0;
    if (body.TryGetValue("acc", out var accValue))
    {
        if (accValue.ValueKind == JsonValueKind.Number)
            acc = accValue.GetInt32();
        else if (accValue.ValueKind == JsonValueKind.String)
            int.TryParse(accValue.GetString(), out acc);
    }

    var ok = await gate.Allow(Field(body, "username"), Field(body, "topic"), acc, ct);
    return ok ? Results.Ok() : Results.Unauthorized();
});
app.MapGet("/internal/mqtt/route", async (string username, HttpRequest request, MqttGate gate, CancellationToken ct) =>
{
    var token = request.Headers["X-Internal-Token"].ToString();
    if (!SecretText.FixedEquals(token, secrets.InternalToken))
        return Results.Unauthorized();
    var route = await gate.Route(username, ct);
    return route is null ? Results.NotFound() : Results.Ok(new { host = route.Host, port = route.Port, tls = route.Tls, prefix = route.Prefix });
});
app.MapPost("/internal/mqtt/identity", async (IdentityBody body, HttpRequest request, Catalog catalog, CancellationToken ct) =>
{
    var token = request.Headers["X-Internal-Token"].ToString();
    if (!SecretText.FixedEquals(token, secrets.InternalToken))
        return Results.Unauthorized();
    var stored = await catalog.BindPublisher(body.Username, body.PublicKey, ct);
    return stored ? Results.NoContent() : Results.NotFound();
});

var api = app.MapGroup("/api");
api.MapPost("/auth/login", async (LoginBody body, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.Login(body.Email, body.Password, ct)));
api.MapGet("/auth/me", async (ClaimsPrincipal principal, AppDb db, CancellationToken ct) =>
{
    var id = UserId(principal);
    var user = await db.Users.FirstAsync(u => u.Id == id, ct);
    return Results.Ok(new { id = user.Id, email = user.Email, name = user.DisplayName, admin = user.IsAdmin, canCreateGroups = user.CanCreateGroups });
}).RequireAuthorization();
api.MapPost("/auth/password", async (PasswordBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.ChangePassword(UserId(principal), body.Current, body.Password, ct);
    return Results.NoContent();
}).RequireAuthorization();

api.Map("/ws/live", async (HttpContext context, LiveHub hub, ClaimsPrincipal principal) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var watch = LiveHub.ParseWatch(context.Request.Query["watch"]);
    if ((watch & (LiveWatch.Activity | LiveWatch.Nodes)) != 0 && !principal.IsInRole("admin"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await hub.RunSession(socket, UserId(principal), watch, context.RequestAborted);
}).RequireAuthorization();

api.MapGet("/connection", (Catalog catalog) => catalog.Connection()).RequireAuthorization();
api.MapGet("/connection/ca", (Catalog catalog) =>
{
    var path = Path.Combine(secrets.CertsDirectory, "ca.crt");
    return File.Exists(path)
        ? Results.File(File.ReadAllBytes(path), "application/x-pem-file", "ca.crt")
        : Results.NotFound();
}).RequireAuthorization();

api.MapGet("/roles", async (ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.Roles(UserId(principal), ct))).RequireAuthorization();
api.MapPost("/roles", async (RoleBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.CreateRole(UserId(principal), body.Name, body.Privileges, ct))).RequireAuthorization();

api.MapGet("/groups", async (ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.Groups(UserId(principal), ct))).RequireAuthorization();
api.MapGet("/tree", async (ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.Tree(UserId(principal), ct))).RequireAuthorization();
api.MapPost("/groups", async (NameBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.CreateGroup(UserId(principal), body.Name, ct))).RequireAuthorization();
api.MapGet("/groups/{id:guid}", async (Guid id, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.GroupDetails(UserId(principal), id, ct))).RequireAuthorization();
api.MapPatch("/groups/{id:guid}", async (Guid id, NameBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.RenameGroup(UserId(principal), id, body.Name, ct);
    return Results.NoContent();
}).RequireAuthorization();
api.MapDelete("/groups/{id:guid}", async (Guid id, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.DeleteGroup(UserId(principal), id, ct);
    return Results.NoContent();
}).RequireAuthorization();
api.MapPost("/groups/{id:guid}/spaces", async (Guid id, NameBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.CreateSpace(UserId(principal), id, body.Name, ct))).RequireAuthorization();
api.MapPost("/groups/{id:guid}/grants", async (Guid id, GrantBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.Grant(UserId(principal), id, body.Email, ct))).RequireAuthorization();

api.MapGet("/spaces/{id:guid}", async (Guid id, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.SpaceDetails(UserId(principal), id, ct))).RequireAuthorization();
api.MapPatch("/spaces/{id:guid}", async (Guid id, NameBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.RenameSpace(UserId(principal), id, body.Name, ct);
    return Results.NoContent();
}).RequireAuthorization();
api.MapDelete("/spaces/{id:guid}", async (Guid id, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.DeleteSpace(UserId(principal), id, ct);
    return Results.NoContent();
}).RequireAuthorization();
api.MapPost("/spaces/{id:guid}/devices", async (Guid id, DeviceBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.CreateDevice(UserId(principal), id, body.Name, body.CanSubscribe, body.CanPublish, ct))).RequireAuthorization();
api.MapDelete("/devices/{id:guid}", async (Guid id, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.DeleteDevice(UserId(principal), id, ct);
    return Results.NoContent();
}).RequireAuthorization();
api.MapPost("/devices/{id:guid}/move", async (Guid id, MoveDeviceBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.MoveDevice(UserId(principal), id, body.SpaceId, ct);
    return Results.NoContent();
}).RequireAuthorization();
api.MapPut("/grants/{id:guid}", async (Guid id, GrantTunnelsBody body, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.SetTunnels(UserId(principal), id, body.Tunnels ?? [], ct);
    return Results.NoContent();
}).RequireAuthorization();
api.MapDelete("/grants/{id:guid}", async (Guid id, ClaimsPrincipal principal, Catalog catalog, CancellationToken ct) =>
{
    await catalog.Revoke(UserId(principal), id, ct);
    return Results.NoContent();
}).RequireAuthorization();

var admin = api.MapGroup("/admin").RequireAuthorization(policy => policy.RequireRole("admin"));
admin.MapGet("/users", async (AdminWork work, CancellationToken ct) => Results.Ok(await work.Users(ct)));
admin.MapPost("/users", async (CreateUserBody body, AdminWork work, CancellationToken ct) =>
    Results.Ok(await work.CreateUser(body.Email, body.Name, body.Password, body.Admin, body.CanCreateGroups, ct)));
admin.MapPatch("/users/{id:guid}", async (Guid id, UpdateUserBody body, AdminWork work, CancellationToken ct) =>
{
    await work.UpdateUser(id, body.Disabled, body.Admin, body.CanCreateGroups, body.Password, ct);
    return Results.NoContent();
});
admin.MapGet("/users/{id:guid}", async (Guid id, AdminWork work, CancellationToken ct) => Results.Ok(await work.UserTree(id, ct)));
admin.MapGet("/sharing", async (AdminWork work, CancellationToken ct) => Results.Ok(await work.Sharing(ct)));
admin.MapGet("/activity", async (AdminWork work, CancellationToken ct) => Results.Ok(await work.Activity(ct)));
admin.MapGet("/nodes", async (AdminWork work, CancellationToken ct) => Results.Ok(await work.Nodes(ct)));
admin.MapPost("/nodes", async (NodeBody body, AdminWork work, CancellationToken ct) =>
    Results.Ok(await work.AddNode(body.Name, body.Host, body.Port, body.Tls, ct)));
admin.MapPatch("/nodes/{id:guid}", async (Guid id, StatusBody body, AdminWork work, CancellationToken ct) =>
{
    await work.SetStatus(id, body.Status, ct);
    return Results.NoContent();
});
admin.MapDelete("/nodes/{id:guid}", async (Guid id, AdminWork work, CancellationToken ct) =>
{
    await work.DeleteNode(id, ct);
    return Results.NoContent();
});
admin.MapPost("/spaces/{id:guid}/move", async (Guid id, MoveBody body, AdminWork work, CancellationToken ct) =>
{
    await work.MoveSpace(id, body.NodeId, ct);
    return Results.NoContent();
});
admin.MapGet("/certificate", (AdminWork work) => Results.Ok(work.Certificate()));
admin.MapPost("/certificate", async (CertificateBody body, AdminWork work, CancellationToken ct) =>
    Results.Ok(await work.ReissueCertificate(body.Host, ct)));
admin.MapGet("/certificate/key", (AdminWork work) =>
    Results.File(Encoding.UTF8.GetBytes(work.ExportKey()), "application/x-pem-file", "server.key"));
admin.MapPost("/certificate/key", async (KeyBody body, AdminWork work, CancellationToken ct) =>
    Results.Ok(await work.ImportKey(body.Pem, ct)));

app.Run();

static Guid UserId(ClaimsPrincipal principal) =>
    Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

static async Task<Dictionary<string, JsonElement>> ReadBody(HttpRequest request, CancellationToken ct)
{
    using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
}

static string? Field(Dictionary<string, JsonElement> body, string name) =>
    body.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

static async Task InitializeAsync(WebApplication app, AppSecrets secrets)
{
    CertificateFiles.Ensure(secrets.CertsDirectory, secrets.PublicHost);
    if (CertificateFiles.ReadHost(secrets.CertsDirectory) is { } storedHost)
        secrets.PublicHost = storedHost;
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var hasher = scope.ServiceProvider.GetRequiredService<Passwords>();
    for (var attempt = 0; attempt < 30; attempt++)
    {
        try
        {
            await db.Database.EnsureCreatedAsync();
            break;
        }
        catch (Exception ex)
        {
            if (attempt == 29)
                throw;
            app.Logger.LogWarning(ex, "База ещё не готова, повтор");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    await EnsureUserColumns(db);
    await EnsureDeviceColumns(db);

    if (!await db.Users.AnyAsync(u => u.IsAdmin))
    {
        var admin = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = secrets.AdminEmail.Trim().ToLowerInvariant(),
            DisplayName = "Администратор",
            IsAdmin = true,
            CanCreateGroups = true,
            CreatedAt = DateTime.UtcNow
        };
        admin.PasswordHash = hasher.HashUser(admin, secrets.AdminPassword);
        db.Users.Add(admin);
    }

    if (!await db.BrokerNodes.AnyAsync())
    {
        db.BrokerNodes.Add(new BrokerNode
        {
            Id = Guid.NewGuid(),
            Name = "local",
            Host = "mosquitto",
            Port = 1883,
            UseTls = false,
            Status = NodeStatus.Open,
            CreatedAt = DateTime.UtcNow
        });
    }

    if (!await db.Roles.AnyAsync(r => r.IsSystem))
    {
        db.Roles.AddRange(
            SystemRole("Наблюдатель", Privilege.View | Privilege.Subscribe),
            SystemRole("Издатель", Privilege.View | Privilege.Subscribe | Privilege.Publish),
            SystemRole("Оператор", Privilege.View | Privilege.Subscribe | Privilege.Publish | Privilege.Credentials),
            SystemRole("Управляющий", Privilege.All));
    }

    await db.SaveChangesAsync();
    await EnsureNameIndexes(db, app.Logger);
    await MigrateGrants(db);
    await db.Database.ExecuteSqlRawAsync("""DROP TABLE IF EXISTS topic_filters""");
    await MapTables.Ensure(db);
    await scope.ServiceProvider.GetRequiredService<StatsStore>().LoadMapAsync(db, CancellationToken.None);
}

static async Task EnsureUserColumns(AppDb db)
{
    await db.Database.ExecuteSqlRawAsync("""
        DO $mig$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'users' AND column_name = 'CanCreateGroups'
          ) THEN
            ALTER TABLE users ADD COLUMN "CanCreateGroups" boolean NOT NULL DEFAULT true;
            ALTER TABLE users ALTER COLUMN "CanCreateGroups" SET DEFAULT false;
          END IF;
        END
        $mig$;
        """);
}

static async Task EnsureDeviceColumns(AppDb db)
{
    await db.Database.ExecuteSqlRawAsync("""
        DO $mig$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'device_logins' AND column_name = 'PublicKey'
          ) THEN
            ALTER TABLE device_logins ADD COLUMN "PublicKey" character varying(64) NOT NULL DEFAULT '';
          END IF;
        END
        $mig$;
        """);
}

static async Task EnsureNameIndexes(AppDb db, ILogger logger)
{
    foreach (var sql in new[]
    {
        """CREATE UNIQUE INDEX IF NOT EXISTS ix_groups_owner_name ON groups ("OwnerUserId", lower("Name"))""",
        """CREATE UNIQUE INDEX IF NOT EXISTS ix_spaces_group_name ON spaces ("GroupId", lower("Name"))"""
    })
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Уникальный индекс имён не создан: уже есть повторы");
        }
    }
}

static async Task MigrateGrants(AppDb db)
{
    await db.Database.ExecuteSqlRawAsync("""
            DO $mig$
            BEGIN
              IF NOT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'grant_tunnels'
              ) THEN
                CREATE TABLE grant_tunnels (
                  "Id" uuid NOT NULL,
                  "GrantId" uuid NOT NULL,
                  "SpaceId" uuid NOT NULL,
                  "Privileges" integer NOT NULL,
                  CONSTRAINT "PK_grant_tunnels" PRIMARY KEY ("Id"),
                  CONSTRAINT "FK_grant_tunnels_access_grants_GrantId" FOREIGN KEY ("GrantId") REFERENCES access_grants ("Id") ON DELETE CASCADE,
                  CONSTRAINT "FK_grant_tunnels_spaces_SpaceId" FOREIGN KEY ("SpaceId") REFERENCES spaces ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX "IX_grant_tunnels_GrantId_SpaceId" ON grant_tunnels ("GrantId", "SpaceId");
                CREATE INDEX "IX_grant_tunnels_SpaceId" ON grant_tunnels ("SpaceId");
              END IF;

              IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'access_grants' AND column_name = 'SpaceId'
              ) THEN
                DELETE FROM access_grants g
                WHERE g."SpaceId" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM spaces s WHERE s."Id" = g."SpaceId");

                INSERT INTO access_grants ("Id", "UserId", "GroupId", "SpaceId", "Privileges", "CreatedAt")
                SELECT gen_random_uuid(), src."UserId", src."GroupId", NULL, 0, MIN(src."CreatedAt")
                FROM (
                  SELECT g."UserId", s."GroupId", g."CreatedAt"
                  FROM access_grants g
                  JOIN spaces s ON s."Id" = g."SpaceId"
                  WHERE g."SpaceId" IS NOT NULL
                ) src
                WHERE NOT EXISTS (
                  SELECT 1 FROM access_grants existing
                  WHERE existing."UserId" = src."UserId"
                    AND existing."GroupId" = src."GroupId"
                    AND existing."SpaceId" IS NULL
                )
                GROUP BY src."UserId", src."GroupId";

                INSERT INTO grant_tunnels ("Id", "GrantId", "SpaceId", "Privileges")
                SELECT gen_random_uuid(), parent."Id", g."SpaceId", g."Privileges"
                FROM access_grants g
                JOIN spaces s ON s."Id" = g."SpaceId"
                JOIN access_grants parent
                  ON parent."UserId" = g."UserId"
                 AND parent."GroupId" = s."GroupId"
                 AND parent."SpaceId" IS NULL
                WHERE g."SpaceId" IS NOT NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM grant_tunnels t
                    WHERE t."GrantId" = parent."Id" AND t."SpaceId" = g."SpaceId"
                  );

                INSERT INTO grant_tunnels ("Id", "GrantId", "SpaceId", "Privileges")
                SELECT gen_random_uuid(), g."Id", s."Id", g."Privileges"
                FROM access_grants g
                JOIN spaces s ON s."GroupId" = g."GroupId"
                WHERE g."SpaceId" IS NULL
                  AND g."GroupId" IS NOT NULL
                  AND g."Privileges" <> 0
                  AND NOT EXISTS (
                    SELECT 1 FROM grant_tunnels t
                    WHERE t."GrantId" = g."Id" AND t."SpaceId" = s."Id"
                  );

                UPDATE grant_tunnels t
                SET "Privileges" = t."Privileges" | g."Privileges"
                FROM access_grants g
                JOIN spaces s ON s."Id" = g."SpaceId"
                JOIN access_grants parent
                  ON parent."UserId" = g."UserId"
                 AND parent."GroupId" = s."GroupId"
                 AND parent."SpaceId" IS NULL
                WHERE g."SpaceId" IS NOT NULL
                  AND t."GrantId" = parent."Id"
                  AND t."SpaceId" = g."SpaceId";

                DELETE FROM access_grants WHERE "SpaceId" IS NOT NULL;
                ALTER TABLE access_grants DROP COLUMN "SpaceId";
                ALTER TABLE access_grants DROP COLUMN "Privileges";
                DELETE FROM access_grants WHERE "GroupId" IS NULL;
                ALTER TABLE access_grants ALTER COLUMN "GroupId" SET NOT NULL;
                DROP INDEX IF EXISTS "IX_access_grants_UserId_GroupId";
                CREATE UNIQUE INDEX "IX_access_grants_UserId_GroupId" ON access_grants ("UserId", "GroupId");
              END IF;
            END
            $mig$;
            """);
}

static RoleTemplate SystemRole(string name, Privilege privileges) => new()
{
    Id = Guid.NewGuid(),
    Name = name,
    Privileges = privileges,
    IsSystem = true
};

record IdentityBody(string? Username, string? PublicKey);
record LoginBody(string? Email, string? Password);
record PasswordBody(string? Current, string? Password);
record NameBody(string? Name);
record RoleBody(string? Name, string[]? Privileges);
record GrantBody(string? Email);
record GrantTunnelsBody(TunnelMark[]? Tunnels);
record DeviceBody(string? Name, bool CanSubscribe, bool CanPublish);
record CreateUserBody(string? Email, string? Name, string? Password, bool Admin, bool CanCreateGroups);
record UpdateUserBody(bool? Disabled, bool? Admin, bool? CanCreateGroups, string? Password);
record NodeBody(string? Name, string? Host, int Port, bool Tls);
record StatusBody(string? Status);
record MoveBody(Guid NodeId);
record MoveDeviceBody(Guid SpaceId);
record CertificateBody(string? Host);
record KeyBody(string? Pem);
