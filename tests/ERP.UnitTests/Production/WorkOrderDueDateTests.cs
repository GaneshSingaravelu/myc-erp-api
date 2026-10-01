using System.Reflection;
using System.Text.Json;
using ERP.Persistence.Context;
using ERP.Persistence.SqlServer.Extensions;
using ERP.Persistence.SqlServer.Migrations.AppDb;
using ERP.Production.Application.Commands;
using ERP.Production.Application.DTOs;
using ERP.Production.Domain.Entities;
using ERP.Production.Domain.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ERP.UnitTests.Production;

// ════════════════════════════════════════════════════════════════════════════════
// P1-g — WorkOrder.DueDate is a business calendar date, not an instant.
//
// WHAT THIS SLICE CHANGED AND WHY
// ===============================
// DueDate was DateTime? / SQL Server datetime2, so it carried a time-of-day component that
// had no business meaning. Two consequences made that unsafe to leave alone:
//
//   * any later UTC normalisation (P1-h) could shift the value across a midnight boundary
//     and silently change WHICH DAY an order was due;
//   * the PostgreSQL Tenant migration lineage (P1-j) has not been generated yet, so leaving
//     the model wrong would have baked a timestamp column into a brand-new lineage.
//
// It is now DateOnly? mapped to `date` on both engines, serialized as "2026-10-01".
//
// The contract tightening is DELIBERATE: "2026-10-01T14:30:00" is now REJECTED rather than
// truncated, because truncating would discard a value the caller clearly meant to send.
//
// SCOPE NOTE: ReleasedAt, CompletedAt, StartedAt and CreatedAt are genuine instants and are
// deliberately untouched. This is not a general DateTime cleanup.
//
// EVIDENCE BOUNDARY: everything here is static / unit / SQLite evidence. Live SQL Server
// migration-and-roundtrip proof is gathered separately in P1-g against a throwaway database;
// PostgreSQL verification belongs to P1-j/P1-k and is NOT claimed here.
// ════════════════════════════════════════════════════════════════════════════════

/// <summary>CLR contract: the domain and the surfaces that carry it use <c>DateOnly?</c>.</summary>
public class WorkOrderDueDateTypeTests
{
    private static Type? DueDateTypeOf<T>()
        => typeof(T).GetProperty("DueDate", BindingFlags.Public | BindingFlags.Instance)?.PropertyType;

    [Fact]
    public void Entity_DueDate_IsNullableDateOnly()
    {
        var property = typeof(WorkOrder).GetProperty(nameof(WorkOrder.DueDate));

        Assert.NotNull(property);
        Assert.Equal(typeof(DateOnly?), property.PropertyType);
        // Guards the direction of the change: a silent revert to a timestamp must fail here.
        Assert.NotEqual(typeof(DateTime?), property.PropertyType);
    }

    [Theory]
    [InlineData(typeof(WorkOrderResponse))]
    [InlineData(typeof(WorkOrderListResponse))]
    [InlineData(typeof(CreateWorkOrderRequest))]
    [InlineData(typeof(UpdateWorkOrderRequest))]
    [InlineData(typeof(CreateWorkOrderCommand))]
    [InlineData(typeof(UpdateWorkOrderCommand))]
    public void EverySurfaceCarryingDueDate_UsesNullableDateOnly(Type surface)
    {
        // The queries and the controller pass DueDate through positionally, so the compiler
        // already enforces them. These six are the declared contract: if one of them drifted
        // back to DateTime?, the wire format would change without any other test noticing.
        var property = surface.GetProperty("DueDate");

        Assert.NotNull(property);
        Assert.Equal(typeof(DateOnly?), property.PropertyType);
    }

    [Fact]
    public void InstantFieldsAreDeliberatelyStillDateTime()
    {
        // Scope evidence, not redundancy: this slice must NOT have converted the real instants.
        Assert.Equal(typeof(DateTime?), DueDateTypeOf<WorkOrderResponse>() is null ? null : typeof(WorkOrderResponse).GetProperty(nameof(WorkOrderResponse.ReleasedAt))!.PropertyType);
        Assert.Equal(typeof(DateTime?), typeof(WorkOrder).GetProperty(nameof(WorkOrder.CompletedAt))!.PropertyType);
        Assert.Equal(typeof(DateTime?), typeof(WorkOrder).GetProperty(nameof(WorkOrder.ReleasedAt))!.PropertyType);
    }
}

/// <summary>EF model contract: the relational store type is <c>date</c>, still nullable.</summary>
public class WorkOrderDueDateModelTests
{
    static WorkOrderDueDateModelTests()
    {
        // Mirrors how the host composes the model: the Production module's configurations live
        // in their own assembly and are registered through AppDbContext.ConfigurationAssemblies.
        var productionAssembly = typeof(ERP.Production.Infrastructure.Configurations.WorkOrderConfiguration).Assembly;
        if (!AppDbContext.ConfigurationAssemblies.Contains(productionAssembly))
            AppDbContext.ConfigurationAssemblies.Add(productionAssembly);
    }

    /// <summary>
    /// Builds the model through the real SQL Server provider seam. No connection is opened —
    /// model building is offline — but the relational store type is only meaningful when a
    /// relational provider is configured, so the seam matters.
    /// </summary>
    private static IModel SqlServerModel()
    {
        // UseSqlServerProvider extends the non-generic builder, so it is applied to the typed
        // builder and the TYPED Options are taken afterwards.
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseSqlServerProvider("Server=(localdb)\\unused;Database=model-only;Trusted_Connection=True;");

        using var context = new AppDbContext(builder.Options);
        return context.Model;
    }

    [Fact]
    public void DueDate_MapsToDate_AndStaysNullable()
    {
        var property = SqlServerModel()
            .FindEntityType(typeof(WorkOrder))!
            .FindProperty(nameof(WorkOrder.DueDate))!;

        Assert.Equal("date", property.GetColumnType());
        Assert.True(property.IsNullable);
        Assert.Equal(typeof(DateOnly?), property.ClrType);
    }

    [Fact]
    public void DueDate_IsNotStoredAsATimestamp()
    {
        // Stated as its own assertion because this is the defect being closed: any datetime
        // store type reintroduces a meaningless time component.
        var columnType = SqlServerModel()
            .FindEntityType(typeof(WorkOrder))!
            .FindProperty(nameof(WorkOrder.DueDate))!
            .GetColumnType();

        Assert.DoesNotContain("datetime", columnType!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DueDate_ColumnType_IsConfiguredEXPLICITLY_NotLeftToConvention()
    {
        // Why this exists: EF's own default store type for DateOnly on SQL Server is ALREADY
        // "date", so asserting the column type alone cannot tell an explicit mapping apart from
        // a convention default. Removing .HasColumnType("date") would leave every other test in
        // this class passing. This asserts the annotation is actually present, so the defensive
        // mapping cannot be deleted silently — which is the whole reason it was added.
        var property = SqlServerModel()
            .FindEntityType(typeof(WorkOrder))!
            .FindProperty(nameof(WorkOrder.DueDate))!;

        var annotation = property.FindAnnotation("Relational:ColumnType");

        Assert.NotNull(annotation);
        Assert.Equal("date", annotation.Value);
    }

    [Fact]
    public void DueDate_NeedsNoValueConverter()
    {
        // The approved contract said: do not introduce a converter unless EF proves one is
        // required. This asserts that it was not required — DateOnly maps natively.
        var property = SqlServerModel()
            .FindEntityType(typeof(WorkOrder))!
            .FindProperty(nameof(WorkOrder.DueDate))!;

        Assert.Null(property.GetValueConverter());
    }

    [Fact]
    public void TheInstantColumnsWereNotConverted()
    {
        // Scope evidence: ReleasedAt / CompletedAt must still be timestamps.
        var entity = SqlServerModel().FindEntityType(typeof(WorkOrder))!;

        Assert.Equal("datetime2", entity.FindProperty(nameof(WorkOrder.ReleasedAt))!.GetColumnType());
        Assert.Equal("datetime2", entity.FindProperty(nameof(WorkOrder.CompletedAt))!.GetColumnType());
    }
}

/// <summary>The SQL Server migration alters exactly one column and nothing else.</summary>
public class WorkOrderDueDateMigrationTests
{
    private static readonly ChangeWorkOrderDueDateToDate Migration = new();

    [Fact]
    public void Up_ContainsExactlyOneOperation()
    {
        // A migration that quietly carried an index, default or unrelated table change would
        // be a much larger review surface than this slice was approved for.
        Assert.Single(Migration.UpOperations);
        Assert.Single(Migration.DownOperations);
    }

    [Fact]
    public void Up_AltersWorkOrdersDueDate_FromDatetime2ToDate()
    {
        var operation = Assert.IsType<AlterColumnOperation>(Migration.UpOperations.Single());

        Assert.Equal("WorkOrders", operation.Table);
        Assert.Equal("DueDate", operation.Name);
        Assert.Equal("date", operation.ColumnType);
        Assert.Equal(typeof(DateOnly), operation.ClrType);
        Assert.True(operation.IsNullable);

        // The "old" side proves this is the intended conversion, not a fresh column.
        Assert.Equal("datetime2", operation.OldColumn.ColumnType);
        Assert.Equal(typeof(DateTime), operation.OldColumn.ClrType);
        Assert.True(operation.OldColumn.IsNullable);
    }

    [Fact]
    public void Down_IsTheExactInverse_ForSchemaOnly()
    {
        // Schema is reversible. DATA is not: Up() discards time components and Down() cannot
        // bring them back. That limitation is documented on the migration itself rather than
        // implied by the presence of a Down().
        var operation = Assert.IsType<AlterColumnOperation>(Migration.DownOperations.Single());

        Assert.Equal("WorkOrders", operation.Table);
        Assert.Equal("DueDate", operation.Name);
        Assert.Equal("datetime2", operation.ColumnType);
        Assert.Equal(typeof(DateTime), operation.ClrType);
        Assert.True(operation.IsNullable);
        Assert.Equal("date", operation.OldColumn.ColumnType);
    }

    [Fact]
    public void Migration_ContainsNoDataManipulation()
    {
        // No UPDATE/DELETE/raw SQL: deciding what a non-midnight due date meant is an owner
        // decision, so the migration must refuse to guess. The deployment precheck covers it.
        var all = Migration.UpOperations.Concat(Migration.DownOperations).ToList();

        Assert.DoesNotContain(all, op => op is UpdateDataOperation);
        Assert.DoesNotContain(all, op => op is DeleteDataOperation);
        Assert.DoesNotContain(all, op => op is SqlOperation);
    }
}

/// <summary>
/// JSON wire contract. No custom converter exists or is wanted — these assert the NATIVE
/// System.Text.Json behaviour on .NET, which is exactly the approved shape.
/// </summary>
public class WorkOrderDueDateJsonTests
{
    // Matches ASP.NET Core's default (camelCase) so the assertions describe the real wire.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Request_DeserializesABareDate()
    {
        const string json = """
            {"orderNumber":"WO-1","productName":"Rod","quantity":5,
             "processTemplateId":"11111111-1111-1111-1111-111111111111",
             "plantId":"22222222-2222-2222-2222-222222222222",
             "dueDate":"2026-10-01"}
            """;

        var request = JsonSerializer.Deserialize<CreateWorkOrderRequest>(json, Web);

        Assert.NotNull(request);
        Assert.Equal(new DateOnly(2026, 10, 1), request.DueDate);
    }

    [Fact]
    public void Response_SerializesExactlyAsADate_NoTimeNoOffset()
    {
        var response = new WorkOrderListResponse(
            Guid.Empty, "WO-1", "Rod", 5, WorkOrderStatus.Draft,
            DueDate: new DateOnly(2026, 10, 1),
            ReleasedAt: null, JobCardCount: 0, CreatedAt: DateTime.UnixEpoch);

        var json = JsonSerializer.Serialize(response, Web);

        Assert.Contains("\"dueDate\":\"2026-10-01\"", json);

        // Asserted on the dueDate VALUE specifically, not by scanning the whole payload: the
        // same object legitimately carries createdAt as a real instant ("1970-01-01T00:00:00Z"),
        // so a blanket "no 00:00:00 anywhere" check would fail on a field this slice must not
        // touch. The point of the slice is that THIS field has no time, offset or zone.
        using var document = JsonDocument.Parse(json);
        var dueDate = document.RootElement.GetProperty("dueDate").GetString();

        Assert.Equal("2026-10-01", dueDate);
        Assert.DoesNotContain("T", dueDate!);
        Assert.DoesNotContain(":", dueDate!);
        Assert.DoesNotContain("Z", dueDate!);
        Assert.DoesNotContain("+", dueDate!);

        // And the instant field is still an instant — scope evidence in the same place.
        Assert.Contains("T", document.RootElement.GetProperty("createdAt").GetString()!);
    }

    [Theory]
    [InlineData("2026-10-01T14:30:00")]
    [InlineData("2026-10-01T00:00:00")]
    [InlineData("2026-10-01T14:30:00Z")]
    [InlineData("2026-10-01T14:30:00+05:30")]
    public void TimestampShapedInput_IsRejected_NotTruncated(string supplied)
    {
        // THE deliberate breaking change. Truncating "2026-10-01T14:30:00" to 2026-10-01 would
        // throw away a value the caller meant; a 400 tells them the contract changed. Note the
        // midnight case is rejected too — the shape is wrong even when the value would survive.
        var json = $"{{\"productName\":\"Rod\",\"quantity\":5,\"dueDate\":\"{supplied}\"}}";

        var ex = Record.Exception(() => JsonSerializer.Deserialize<UpdateWorkOrderRequest>(json, Web));

        Assert.IsType<JsonException>(ex);
    }

    [Theory]
    [InlineData("01/10/2026")]
    [InlineData("2026-13-01")]
    [InlineData("not-a-date")]
    public void MalformedDates_AreAlsoRejected(string supplied)
    {
        var json = $"{{\"productName\":\"Rod\",\"quantity\":5,\"dueDate\":\"{supplied}\"}}";

        Assert.IsType<JsonException>(
            Record.Exception(() => JsonSerializer.Deserialize<UpdateWorkOrderRequest>(json, Web)));
    }

    [Fact]
    public void NullDueDate_RoundTripsAsNull()
    {
        const string json = """{"productName":"Rod","quantity":5,"dueDate":null,"notes":null}""";

        var request = JsonSerializer.Deserialize<UpdateWorkOrderRequest>(json, Web);
        Assert.NotNull(request);
        Assert.Null(request.DueDate);

        Assert.Contains("\"dueDate\":null", JsonSerializer.Serialize(request, Web));
    }

    [Fact]
    public void OmittedDueDate_DefaultsToNullOnCreate()
    {
        const string json = """
            {"orderNumber":"WO-1","productName":"Rod","quantity":5,
             "processTemplateId":"11111111-1111-1111-1111-111111111111",
             "plantId":"22222222-2222-2222-2222-222222222222"}
            """;

        var request = JsonSerializer.Deserialize<CreateWorkOrderRequest>(json, Web);

        Assert.NotNull(request);
        Assert.Null(request.DueDate);
    }
}

/// <summary>
/// Relational round-trip on SQLite. Chosen because it is a real relational provider that runs
/// in-process; it proves the value survives a database boundary unchanged. SQL Server's own
/// migration-and-roundtrip evidence is gathered separately against a throwaway database.
/// </summary>
public class WorkOrderDueDateRelationalTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    static WorkOrderDueDateRelationalTests()
    {
        var productionAssembly = typeof(ERP.Production.Infrastructure.Configurations.WorkOrderConfiguration).Assembly;
        if (!AppDbContext.ConfigurationAssemblies.Contains(productionAssembly))
            AppDbContext.ConfigurationAssemblies.Add(productionAssembly);
    }

    public WorkOrderDueDateRelationalTests()
    {
        // Held open for the fixture's lifetime — an in-memory SQLite database exists only as
        // long as a connection to it does.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options);

        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static WorkOrder NewWorkOrder(string orderNumber, DateOnly? dueDate) => new(
        orderNumber,
        productName: "Steel Rod Type A",
        quantity: 10,
        processTemplateId: Guid.NewGuid(),
        plantId: Guid.NewGuid(),
        dueDate: dueDate);

    private async Task<WorkOrder> ReloadAsync(Guid id)
    {
        _context.ChangeTracker.Clear(); // force a real read, not an identity-map hit
        return await _context.Set<WorkOrder>().SingleAsync(wo => wo.Id == id);
    }

    [Theory]
    [InlineData(2026, 10, 1)]
    [InlineData(2026, 1, 1)]   // year boundary
    [InlineData(2026, 12, 31)] // year boundary
    [InlineData(2024, 2, 29)]  // leap day
    public async Task DueDate_SurvivesAStoreRoundTrip_Unchanged(int year, int month, int day)
    {
        var expected = new DateOnly(year, month, day);
        var workOrder = NewWorkOrder($"WO-{year}{month:00}{day:00}", expected);

        _context.Set<WorkOrder>().Add(workOrder);
        await _context.SaveChangesAsync();

        var reloaded = await ReloadAsync(workOrder.Id);

        Assert.Equal(expected, reloaded.DueDate);
    }

    [Fact]
    public async Task NullDueDate_SurvivesAStoreRoundTrip()
    {
        var workOrder = NewWorkOrder("WO-NULL", null);

        _context.Set<WorkOrder>().Add(workOrder);
        await _context.SaveChangesAsync();

        Assert.Null((await ReloadAsync(workOrder.Id)).DueDate);
    }

    [Fact]
    public async Task Update_ChangesTheStoredDate_AndCanClearIt()
    {
        var workOrder = NewWorkOrder("WO-UPD", new DateOnly(2026, 10, 1));
        _context.Set<WorkOrder>().Add(workOrder);
        await _context.SaveChangesAsync();

        // Draft status is required by WorkOrder.Update — the entity is created in Draft.
        workOrder.Update("Steel Rod Type B", 20, new DateOnly(2026, 11, 15), notes: null);
        await _context.SaveChangesAsync();
        Assert.Equal(new DateOnly(2026, 11, 15), (await ReloadAsync(workOrder.Id)).DueDate);

        var tracked = await _context.Set<WorkOrder>().SingleAsync(wo => wo.Id == workOrder.Id);
        tracked.Update("Steel Rod Type B", 20, null, notes: null);
        await _context.SaveChangesAsync();
        Assert.Null((await ReloadAsync(workOrder.Id)).DueDate);
    }

    [Fact]
    public async Task TheStoredValueCarriesNoTimeComponent()
    {
        // Reads the raw stored text, bypassing EF's materialisation. On a date column there is
        // nothing after the day — which is the whole point of the slice.
        var workOrder = NewWorkOrder("WO-RAW", new DateOnly(2026, 10, 1));
        _context.Set<WorkOrder>().Add(workOrder);
        await _context.SaveChangesAsync();

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DueDate FROM WorkOrders WHERE OrderNumber = 'WO-RAW';";
        var raw = Convert.ToString(await command.ExecuteScalarAsync());

        Assert.NotNull(raw);
        Assert.StartsWith("2026-10-01", raw);
        Assert.DoesNotContain(":", raw);
    }
}
