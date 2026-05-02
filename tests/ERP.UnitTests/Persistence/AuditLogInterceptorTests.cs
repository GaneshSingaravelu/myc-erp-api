using ERP.Application.Abstractions.Identity;
using ERP.Persistence.Interceptors;
using ERP.SharedKernel.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// Tests for AuditLogInterceptor — verifies that AuditLog records are written
/// for Create / Update / Delete operations on AuditableEntity subclasses.
/// Uses an in-memory DbContext with a dedicated test entity and AuditLog.
/// </summary>
public class AuditLogInterceptorTests : IDisposable
{
    private readonly AuditLogTestDbContext _context;
    private readonly Guid _userId = Guid.NewGuid();

    public AuditLogInterceptorTests()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(_userId.ToString());
        currentUser.SetupGet(c => c.IpAddress).Returns("192.168.1.1");
        currentUser.SetupGet(c => c.UserAgent).Returns("TestAgent/1.0");

        var auditableInterceptor = new AuditableEntityInterceptor(currentUser.Object);
        var auditLogInterceptor = new AuditLogInterceptor(currentUser.Object);

        var options = new DbContextOptionsBuilder<AuditLogTestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(auditableInterceptor, auditLogInterceptor)
            .Options;

        _context = new AuditLogTestDbContext(options);
    }

    [Fact]
    public async Task Create_Entity_Writes_AuditLog_With_NewValues()
    {
        var widget = new TestWidget { Name = "Gear Box", Quantity = 10 };
        _context.Widgets.Add(widget);
        await _context.SaveChangesAsync();

        var log = await _context.Set<AuditLog>().SingleAsync();

        Assert.Equal("Create", log.Action);
        Assert.Equal(nameof(TestWidget), log.EntityType);
        Assert.Equal(widget.Id.ToString(), log.EntityId);
        Assert.Equal(_userId, log.UserId);
        Assert.Equal("192.168.1.1", log.IpAddress);
        Assert.Equal("TestAgent/1.0", log.UserAgent);
        Assert.Null(log.OldValues);
        Assert.NotNull(log.NewValues);
        Assert.Contains("Gear Box", log.NewValues);
    }

    [Fact]
    public async Task Update_Entity_Writes_AuditLog_With_OldAndNewValues()
    {
        var widget = new TestWidget { Name = "Sprocket", Quantity = 5 };
        _context.Widgets.Add(widget);
        await _context.SaveChangesAsync();

        widget.Name = "Sprocket v2";
        widget.Quantity = 20;
        await _context.SaveChangesAsync();

        var logs = await _context.Set<AuditLog>().OrderBy(l => l.CreatedAt).ToListAsync();

        Assert.Equal(2, logs.Count);
        var updateLog = logs[1];

        Assert.Equal("Update", updateLog.Action);
        Assert.Equal(nameof(TestWidget), updateLog.EntityType);
        Assert.NotNull(updateLog.OldValues);
        Assert.NotNull(updateLog.NewValues);
        Assert.Contains("Sprocket\"", updateLog.OldValues);
        Assert.Contains("Sprocket v2", updateLog.NewValues);
    }

    [Fact]
    public async Task Delete_Entity_Writes_AuditLog_As_Update_Via_SoftDelete()
    {
        var widget = new TestWidget { Name = "Coupling", Quantity = 3 };
        _context.Widgets.Add(widget);
        await _context.SaveChangesAsync();

        _context.Widgets.Remove(widget);
        await _context.SaveChangesAsync();

        // AuditableEntityInterceptor converts Delete → soft-delete (Modified with IsDeleted=true)
        var logs = await _context.Set<AuditLog>().OrderBy(l => l.CreatedAt).ToListAsync();

        Assert.Equal(2, logs.Count);
        var softDeleteLog = logs[1];

        // Soft-delete is intercepted by AuditableEntityInterceptor as Modified, not Deleted
        Assert.Equal("Update", softDeleteLog.Action);
        Assert.Equal(nameof(TestWidget), softDeleteLog.EntityType);
        Assert.NotNull(softDeleteLog.OldValues);
        Assert.NotNull(softDeleteLog.NewValues);
    }

    [Fact]
    public async Task AuditLog_Insert_Does_Not_Recurse()
    {
        var widget = new TestWidget { Name = "Valve", Quantity = 1 };
        _context.Widgets.Add(widget);
        await _context.SaveChangesAsync();

        // Should be exactly one AuditLog (for the widget), not two (one for widget + one for the AuditLog itself)
        var count = await _context.Set<AuditLog>().CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Multiple_Entities_Saved_Together_Produce_Multiple_AuditLogs()
    {
        _context.Widgets.AddRange(
            new TestWidget { Name = "Bolt", Quantity = 100 },
            new TestWidget { Name = "Nut", Quantity = 200 });
        await _context.SaveChangesAsync();

        var count = await _context.Set<AuditLog>().CountAsync();
        Assert.Equal(2, count);
    }

    public void Dispose() => _context.Dispose();
}

// ── Test helpers ─────────────────────────────────────────────────────────────

public class TestWidget : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public class AuditLogTestDbContext(DbContextOptions<AuditLogTestDbContext> options) : DbContext(options)
{
    public DbSet<TestWidget> Widgets => Set<TestWidget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TestWidget>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Ignore(e => e.DomainEvents);
            b.HasQueryFilter(e => !e.IsDeleted);
        });

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Ignore(e => e.DomainEvents);
        });
    }
}
