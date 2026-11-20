using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenPost.Api.Data;
using OpenPost.Api.Data.Entities;
using OpenPost.Api.Mail;
using OpenPost.Api.Mail.Authentication;
using OpenPost.Api.Mail.Inbound;
using OpenPost.Api.Mail.Outbound;
using OpenPost.Api.Middleware;
using OpenPost.Api.Security;
using OpenPost.Api.Services;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging
builder.Host.UseSerilog((context, config) => config
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "OpenPost")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "openpost-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        restrictedToMinimumLevel: LogEventLevel.Information));

// ---------------------------------------------------------------- options
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<MailServerOptions>(builder.Configuration.GetSection("MailServer"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<SpamOptions>(builder.Configuration.GetSection("Spam"));
builder.Services.Configure<AntivirusOptions>(builder.Configuration.GetSection("Antivirus"));
builder.Services.Configure<DeliveryOptions>(builder.Configuration.GetSection("Delivery"));

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) || jwtOptions.SigningKey.Length < 32)
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Jwt:SigningKey must be configured with at least 32 characters. " +
            "Set the JWT__SIGNINGKEY environment variable.");

    jwtOptions.SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    Log.Warning("No Jwt:SigningKey configured - generated an ephemeral key. " +
                "Sessions will not survive a restart; set Jwt__SigningKey for any real deployment.");
}
builder.Services.Configure<JwtOptions>(o =>
{
    o.Issuer = jwtOptions.Issuer;
    o.Audience = jwtOptions.Audience;
    o.SigningKey = jwtOptions.SigningKey;
    o.AccessTokenMinutes = jwtOptions.AccessTokenMinutes;
    o.RefreshTokenDays = jwtOptions.RefreshTokenDays;
});

// ---------------------------------------------------------------- database
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

builder.Services.AddDbContext<OpenPostDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
        npgsql.CommandTimeout(60);
    }));

builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 10;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddRoles<AppRole>()
    .AddEntityFrameworkStores<OpenPostDbContext>()
    .AddDefaultTokenProviders();

// ---------------------------------------------------------------- auth
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.RequireSystemAdmin, policy => policy.RequireRole(Policies.SystemAdmin));
    options.AddPolicy(Policies.RequireAdmin, policy => policy.RequireRole(Policies.SystemAdmin, Policies.DomainAdmin));
    options.FallbackPolicy = null;
});

// ---------------------------------------------------------------- app services
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IMailboxAccess, MailboxAccess>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<DatabaseInitializer>();

builder.Services.AddSingleton<IMessageStorage, LocalDiskStorage>();
builder.Services.AddScoped<IMessageStore, MessageStore>();
builder.Services.AddScoped<RecipientResolver>();
builder.Services.AddScoped<MailRouter>();
builder.Services.AddScoped<IDkimSigner, DkimSigningService>();
builder.Services.AddScoped<IOutboundQueue, OutboundQueue>();
builder.Services.AddScoped<MimeFactory>();
builder.Services.AddScoped<IMailSubmissionService, MailSubmissionService>();

builder.Services.AddScoped<AuthenticationEvaluator>();
builder.Services.AddScoped<HeuristicSpamFilter>();
builder.Services.AddScoped<ISpamFilter, SpamFilterChain>();

if (builder.Configuration.GetValue<bool>("Antivirus:Enabled"))
    builder.Services.AddScoped<IAntivirusScanner, ClamAvScanner>();
else
    builder.Services.AddScoped<IAntivirusScanner, NullAntivirusScanner>();

builder.Services.AddSingleton<IMxResolver, MxResolver>();
builder.Services.AddHttpClient("rspamd", client => client.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddSingleton<OutboundDeliveryWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboundDeliveryWorker>());
builder.Services.AddHostedService<SmtpServerHostedService>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "ready" })
    .AddCheck<StorageHealthCheck>("storage", tags: new[] { "ready" });

// ---------------------------------------------------------------- web plumbing
var allowedOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "http://localhost:4200")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options => options.AddPolicy("web", policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 10;
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("api", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 600;
        limiter.QueueLimit = 20;
    });
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

// ---------------------------------------------------------------- pipeline
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseSerilogRequestLogging();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseCors("web");
app.UseRateLimiter();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapOpenApi("/openapi/{documentName}.json");

// Serve the compiled Angular app for any non-API route (single-page app fallback).
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
{
    app.MapFallbackToFile("index.html");
}

// ---------------------------------------------------------------- start-up
await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await initializer.RunAsync();
            break;
        }
        catch (Exception ex) when (attempt < 10)
        {
            Log.Warning("Database not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

Log.Information("OpenPost API starting: {Environment}", app.Environment.EnvironmentName);
app.Run();
