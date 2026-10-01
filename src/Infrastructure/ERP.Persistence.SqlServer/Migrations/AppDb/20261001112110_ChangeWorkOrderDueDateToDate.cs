using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP.Persistence.SqlServer.Migrations.AppDb
{
    /// <summary>
    /// WorkOrders.DueDate: datetime2 NULL -> date NULL.
    ///
    /// DueDate is a business calendar date, not an instant, so a time-of-day component was
    /// always meaningless on it. See WorkOrder.DueDate.
    ///
    /// NOT REVERSIBLE FOR DATA. Up() DISCARDS any time component present in existing rows.
    /// Down() restores the datetime2 column type, and surviving values read back at midnight,
    /// but the discarded times are gone -- rollback recovers the schema, never the data.
    ///
    /// REQUIRED BEFORE APPLYING THIS TO ANY DATABASE WHOSE ROWS HAVE NOT BEEN VERIFIED:
    /// run a READ-ONLY check for non-null DueDate values carrying a non-midnight time, e.g.
    /// WHERE DueDate IS NOT NULL AND CAST(DueDate AS time) &lt;> CAST('00:00:00' AS time).
    /// If any row matches, STOP -- do not apply this migration and do not silently truncate.
    /// Escalate for an owner / data-owner decision. No data-cleanup operation is included
    /// here on purpose: deciding what a non-midnight due date meant is a business decision,
    /// not something a migration should guess.
    ///
    /// Verified read-only at the time of authoring: local ERP_Dev had 1 WorkOrder with a NULL
    /// DueDate and ERP_TestCo had none, so 0 rows were affected locally. That clears those two
    /// databases only.
    /// </summary>
    public partial class ChangeWorkOrderDueDateToDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateOnly>(
                name: "DueDate",
                table: "WorkOrders",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTime>(
                name: "DueDate",
                table: "WorkOrders",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);
        }
    }
}
