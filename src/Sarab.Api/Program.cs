using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Sarab.Api.Data;
using Sarab.Api.Domain;
using Sarab.Api.Hubs;
using Sarab.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("dev", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173", "https://localhost:5173", "http://localhost:5174", "https://localhost:5174")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddSingleton<PromptPackValidator>();
builder.Services.AddScoped<ScoringEngine>();
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddHostedService<RoomClockService>();
builder.Services.AddHttpClient<IDevBotBrain, DevBotBrain>();
builder.Services.AddHostedService<DevBotRunnerService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAvatarAssetStorage, AvatarAssetStorage>();

var connectionString = builder.Configuration.GetConnectionString("SarabDb")
    ?? builder.Configuration["DATABASE_URL"];
var hasDatabase = !string.IsNullOrWhiteSpace(connectionString);

if (hasDatabase)
{
    builder.Services.AddDbContext<SarabDbContext>(options => options.UseNpgsql(connectionString));
    builder.Services.AddScoped<IContentStore, DbContentStore>();
}
else
{
    builder.Services.AddSingleton<IContentStore, InMemoryContentStore>();
}

var supabaseIssuer = builder.Configuration["Supabase:JwtIssuer"];
var supabaseAudience = builder.Configuration["Supabase:JwtAudience"] ?? "authenticated";
var hasAuth = !string.IsNullOrWhiteSpace(supabaseIssuer);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        if (hasAuth)
        {
            options.Authority = supabaseIssuer;
            options.Audience = supabaseAudience;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true
            };
        }
        else
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateLifetime = false,
                SignatureValidator = (token, _) => new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(token)
            };
        }
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy =>
    {
        policy.RequireAssertion(context => !hasAuth || HasAdminClaim(context.User));
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseCors("dev");
}

if (hasDatabase)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SarabDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true,
    database = hasDatabase ? "postgres" : "memory",
    auth = hasAuth ? "supabase" : "dev-open"
}));

app.MapGet("/api/catalog/categories", async (IContentStore store, CancellationToken ct) =>
    Results.Ok(await store.GetCatalogAsync(ct)));

app.MapGet("/api/avatar/parts", async (IContentStore store, CancellationToken ct) =>
    Results.Ok(await store.GetAvatarPartsAsync(true, ct)));

app.MapGet("/api/summaries/{roomCode}", async (string roomCode, IContentStore store, CancellationToken ct) =>
    Results.Ok(await store.GetRecentSummariesAsync(roomCode.ToUpperInvariant(), ct)));

var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");

admin.MapGet("/packs", async (IContentStore store, CancellationToken ct) =>
    Results.Ok(await store.GetAdminPacksAsync(ct)));

admin.MapGet("/avatar/parts", async (IContentStore store, CancellationToken ct) =>
    Results.Ok(await store.GetAvatarPartsAsync(false, ct)));

admin.MapPost("/avatar/parts", async (
    HttpRequest request,
    IContentStore store,
    IAvatarAssetStorage storage,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Avatar part upload must be multipart form data." });
    }

    try
    {
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? throw new InvalidOperationException("PNG file is required.");
        var imageUrl = await storage.SaveAsync(file, ct);
        var part = new AvatarPartDto(
            Guid.NewGuid(),
            ParsePartType(GetRequiredFormValue(form, "type")),
            GetRequiredFormValue(form, "name")[..Math.Min(120, GetRequiredFormValue(form, "name").Length)].Trim(),
            imageUrl,
            ParseBool(form["supportsMale"].FirstOrDefault()),
            ParseBool(form["supportsFemale"].FirstOrDefault()),
            ParseBool(form["active"].FirstOrDefault(), true),
            ParseBool(form["supportsMale"].FirstOrDefault()) ? ParseTransform(form["maleTransform"].FirstOrDefault()) : null,
            ParseBool(form["supportsFemale"].FirstOrDefault()) ? ParseTransform(form["femaleTransform"].FirstOrDefault()) : null);

        ValidateAvatarPart(part);
        var saved = await store.SaveAvatarPartAsync(part, ct);
        return Results.Created($"/api/admin/avatar/parts/{saved.Id}", saved);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

admin.MapPut("/avatar/parts/{partId:guid}", async (
    Guid partId,
    AvatarPartUpdateRequest update,
    IContentStore store,
    CancellationToken ct) =>
{
    try
    {
        ValidateAvatarUpdate(update);
        var saved = await store.UpdateAvatarPartAsync(partId, update, ct);
        return saved is null ? Results.NotFound() : Results.Ok(saved);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

admin.MapDelete("/avatar/parts/{partId:guid}", async (Guid partId, IContentStore store, CancellationToken ct) =>
{
    await store.DeleteAvatarPartAsync(partId, ct);
    return Results.NoContent();
});

admin.MapPost("/packs/validate", (PromptPackUpload upload, PromptPackValidator validator) =>
    Results.Ok(validator.Validate(upload)));

admin.MapPost("/packs", async (PromptPackUpload upload, PromptPackValidator validator, IContentStore store, CancellationToken ct) =>
{
    var validation = validator.Validate(upload);
    if (!validation.Valid)
    {
        return Results.BadRequest(validation);
    }

    var pack = await store.SavePackAsync(upload, ct);
    return Results.Created($"/api/admin/packs/{pack.Id}", new AdminPackDto(
        pack.Id,
        pack.Name,
        PromptPackValidator.FormatLanguage(pack.Language),
        pack.Active,
        pack.Categories.Count,
        pack.Categories.Sum(x => x.Rounds.Count)));
});

admin.MapPost("/packs/{packId:guid}/activate", async (Guid packId, IContentStore store, CancellationToken ct) =>
{
    await store.SetPackActiveAsync(packId, true, ct);
    return Results.NoContent();
});

admin.MapPost("/packs/{packId:guid}/deactivate", async (Guid packId, IContentStore store, CancellationToken ct) =>
{
    await store.SetPackActiveAsync(packId, false, ct);
    return Results.NoContent();
});

admin.MapDelete("/packs/{packId:guid}", async (Guid packId, IContentStore store, CancellationToken ct) =>
{
    await store.DeletePackAsync(packId, ct);
    return Results.NoContent();
});

admin.MapDelete("/history", async (IContentStore store, CancellationToken ct) =>
{
    await store.DeleteHistoryAsync(ct);
    return Results.NoContent();
});

app.MapHub<GameHub>("/hubs/game");
app.MapFallbackToFile("index.html");

app.Run();

static bool HasAdminClaim(ClaimsPrincipal user)
{
    if (user.HasClaim("role", "admin") || user.IsInRole("admin"))
    {
        return true;
    }

    var appMetadata = user.FindFirst("app_metadata")?.Value;
    return appMetadata?.Contains("admin", StringComparison.OrdinalIgnoreCase) == true;
}

static string GetRequiredFormValue(IFormCollection form, string key)
{
    var value = form[key].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{key} is required.");
    }

    return value;
}

static bool ParseBool(string? value, bool fallback = false)
{
    return bool.TryParse(value, out var parsed) ? parsed : fallback;
}

static AvatarPartType ParsePartType(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "face" => AvatarPartType.Face,
        "headwear" => AvatarPartType.Headwear,
        _ => AvatarPartType.Clothes
    };
}

static AvatarLayerTransformDto ParseTransform(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return InMemoryContentStore.NormalizeTransform(null);
    }

    var transform = JsonSerializer.Deserialize<AvatarLayerTransformDto>(value, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    return InMemoryContentStore.NormalizeTransform(transform);
}

static void ValidateAvatarPart(AvatarPartDto part)
{
    if (string.IsNullOrWhiteSpace(part.Name))
    {
        throw new InvalidOperationException("Name is required.");
    }

    if (!part.SupportsMale && !part.SupportsFemale)
    {
        throw new InvalidOperationException("Avatar part must support at least one gender.");
    }
}

static void ValidateAvatarUpdate(AvatarPartUpdateRequest update)
{
    if (string.IsNullOrWhiteSpace(update.Name))
    {
        throw new InvalidOperationException("Name is required.");
    }

    if (!update.SupportsMale && !update.SupportsFemale)
    {
        throw new InvalidOperationException("Avatar part must support at least one gender.");
    }
}

public partial class Program;
