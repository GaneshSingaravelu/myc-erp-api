using ERP.Api.Middleware;
using ERP.Application.Abstractions.Behaviors;
using ERP.Production.Application.Behaviors;
using ERP.Identity.Extensions;
using ERP.Identity.Seed;
using ERP.Infrastructure.Extensions;
using ERP.Infrastructure.Tenant;
using ERP.Persistence.Extensions;
using ERP.Production.Infrastructure.Extensions;
using ERP.Workflow.Infrastructure.Extensions;
using Mediator;
using Serilog;

// ============================================================================
// PROGRAM.CS — THE COMPOSITION ROOT
// ============================================================================
//
// WHAT IS A COMPOSITION ROOT? (Interview Question!)
// ==================================================
// The Composition Root is the SINGLE place where the entire application is
// assembled. It's where all the DI registrations happen, all middleware is
// configured, and the app starts.
//
// Think of it like a car assembly factory:
// - Engine (Persistence) is built separately
// - Transmission (Infrastructure) is built separately
// - Body (Identity, Notifications) is built separately
// - Program.cs ASSEMBLES them all into a working car
//
// RULE: Only Program.cs references ALL projects. No other project should
// reference more than 2-3 other projects.
//
// ============================================================================
// STEP 1: CREATE THE APPLICATION BUILDER
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ──────────────────────────────────────────────────────
// SERILOG — Structured logging (replaces default logging)
// ──────────────────────────────────────────────────────
// WHY SERILOG?
// - Structured logs: Log.Information("Created {@JobCard}", jobCard)
//   stores the object as structured data, not just a string
// - Multiple sinks: write to Console + File + Seq simultaneously
// - Enrichment: automatically adds RequestId, MachineName, etc.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/erp-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// ============================================================================
// STEP 2: REGISTER ALL SERVICES IN DI CONTAINER
// ============================================================================
// This is where Dependency Injection is configured.
// Each project provides an extension method to register its services.

// --- Core: CQRS with Mediator (MIT-licensed replacement for MediatR RPL-1.5) ---
// Source generator discovers all handlers in referenced assemblies at compile time
builder.Services.AddMediator(o => o.ServiceLifetime = ServiceLifetime.Scoped);

// Pipeline behaviors — run in registration order before every handler
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PlantAuthorizationBehavior<,>));
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

// --- Persistence: Repositories, Unit of Work, Interceptors ---
builder.Services.AddPersistence();

// --- Infrastructure: Multi-tenancy, Caching, Current User ---
builder.Services.AddInfrastructure(builder.Configuration);

// --- Identity: JWT Authentication, Token Service ---
builder.Services.AddIdentityServices(builder.Configuration);

// --- Workflow Module: Process Templates ---
builder.Services.AddWorkflowModule();

// --- Production Module: Work Orders, Job Cards ---+_
builder.Services.AddProductionModule();

// --- Notifications: SignalR ---
builder.Services.AddSignalR();

// --- API Controllers ---
builder.Services.AddControllers();

// --- Swagger (API documentation) ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "ERP API",
        Version = "v1",
        Description = "Enterprise Resource Planning API — Production, Workflow, Identity"
    });

    // Add JWT Bearer token input to Swagger UI
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "Enter your JWT token. Example: eyJhbGciOiJIUzI1NiIs..."
    });

    // The reference must carry the host document AND that document's components must be
    // registered in its workspace. Without both, Microsoft.OpenApi 2.x cannot resolve the
    // reference target and serialises the requirement as an empty "security": [{}], which
    // leaves Swagger UI with no reason to attach the Authorization header.
    options.AddSecurityRequirement(document =>
    {
        document.RegisterComponents();

        return new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", document),
                new List<string>()
            }
        };
    });

    // Gives Swagger UI a box for the tenant header TenantMiddleware already requires.
    // Documentation only — it changes nothing about how the header is validated.
    options.OperationFilter<ERP.Api.Swagger.TenantHeaderOperationFilter>();
});

// --- CORS (Cross-Origin Resource Sharing) ---
// Required because Angular (http://localhost:4200) and API (http://localhost:5000)
// run on different ports. Without CORS, the browser blocks the requests.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins(
                builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:4200"])
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials(); // Required for SignalR
    });
});

// --- Health Checks ---
builder.Services.AddHealthChecks();

// ============================================================================
// STEP 3: BUILD THE APPLICATION
// ============================================================================
var app = builder.Build();

// ============================================================================
// STEP 4: CONFIGURE THE HTTP REQUEST PIPELINE (Middleware)
// ============================================================================
// IMPORTANT: Middleware order matters! Requests flow top-to-bottom,
// responses flow bottom-to-top.
//
// Request →  [Exception]  →  [CORS]  →  [Auth]  →  [Tenant]  →  [Controller]
// Response ← [Exception]  ← [CORS]  ← [Auth]  ← [Tenant]  ← [Controller]

// 1. Global Exception Handler (FIRST — catches ALL exceptions from downstream)
app.UseMiddleware<GlobalExceptionMiddleware>();

// 2. Swagger (development only)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 3. HTTPS Redirection
app.UseHttpsRedirection();

// 4. CORS (must be before Auth)
app.UseCors("AllowAngular");

// 5. Authentication (validates JWT token, sets HttpContext.User)
app.UseAuthentication();

// 6. Authorization (checks [Authorize] attributes)
app.UseAuthorization();

// 7. Tenant Resolution (reads X-Tenant-Id header, resolves tenant)
// ── WHY AFTER AUTH, NOT BEFORE? ──
// JWT auth uses a GLOBAL signing key (not per-tenant), so it doesn't need tenant context.
// Running auth first means unauthenticated requests are rejected early — before we spend
// time looking up tenant info and establishing a database connection.
// The tenant_id is also embedded as a JWT claim, so downstream code can cross-check
// the header tenant against the token's tenant for extra security.
app.UseMiddleware<TenantMiddleware>();

// 8. Serilog request logging (logs every HTTP request with timing)
app.UseSerilogRequestLogging();

// 9. Map Controllers (routes requests to controller actions)
app.MapControllers();

// 10. SignalR hubs — INTENTIONALLY NOT MAPPED (R2-DEV-02B-2A).
// ErpNotificationHub (/hubs/notifications) is disabled: nothing publishes to it, no client
// uses it, and its connection/group authorization is not yet tenant-safe. The hub class and
// AddSignalR() stay in place for Phase 3. Re-mapping it requires explicit security approval;
// ArchitectureTests.NotificationHub_Should_Not_Be_Mapped fails if a MapHub call returns.

// 11. Health Check endpoint
app.MapHealthChecks("/health");

// ============================================================================
// STEP 5: START THE APPLICATION
// ============================================================================
// ============================================================================
// STEP 6: SEED DEFAULT IDENTITY DATA (runs on first startup only)
// ============================================================================
Log.Information("Starting ERP API...");

try
{
    // Seed default admin user, roles, and permissions
    await IdentitySeeder.SeedAsync(app.Services);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ERP API terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
