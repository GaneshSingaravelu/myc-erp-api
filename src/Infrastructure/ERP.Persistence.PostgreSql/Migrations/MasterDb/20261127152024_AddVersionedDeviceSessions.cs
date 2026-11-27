using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP.Persistence.PostgreSql.Migrations.MasterDb
{
    /// <inheritdoc />
    public partial class AddVersionedDeviceSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSessions_RefreshToken",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "RefreshToken",
                table: "UserSessions");

            migrationBuilder.AddColumn<string>(
                name: "RefreshTokenHash",
                table: "UserSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "RotationVersion",
                table: "UserSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // R2-DEV-03A CUTOVER (owner policy: every existing session must log in again).
            // The plaintext RefreshToken column is already dropped above, so legacy refresh tokens
            // cannot be verified any more. Every existing row is revoked (keeping an earlier
            // revocation's reason/time) and given a unique placeholder digest so the unique index
            // below can be created. "legacy-" + 32 hex is not a 64-char hex HMAC digest, so it can
            // never match a presented token.
            migrationBuilder.Sql("""
                UPDATE "UserSessions"
                SET "RevokedReason"    = CASE WHEN NOT "IsRevoked" THEN 'Cutover-R2-DEV-03A' ELSE "RevokedReason" END,
                    "RevokedAt"        = CASE WHEN NOT "IsRevoked" THEN now() ELSE "RevokedAt" END,
                    "IsRevoked"        = TRUE,
                    "RefreshTokenHash" = 'legacy-' || replace("Id"::text, '-', '');
                """);

            migrationBuilder.CreateTable(
                name: "ConsumedRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccessTokenJti = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RotationVersion = table.Column<int>(type: "integer", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumedRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsumedRefreshTokens_UserSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "UserSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_RefreshTokenHash",
                table: "UserSessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConsumedRefreshTokens_RetainUntil",
                table: "ConsumedRefreshTokens",
                column: "RetainUntil");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumedRefreshTokens_SessionId",
                table: "ConsumedRefreshTokens",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumedRefreshTokens_TokenHash",
                table: "ConsumedRefreshTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsumedRefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_UserSessions_RefreshTokenHash",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "RefreshTokenHash",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "RotationVersion",
                table: "UserSessions");

            migrationBuilder.AddColumn<string>(
                name: "RefreshToken",
                table: "UserSessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            // Rollback cannot restore refresh tokens (only digests were kept). Give every row a
            // unique, unusable value so the restored unique index can be created; all sessions
            // stay revoked and every user logs in again on the old version.
            migrationBuilder.Sql("""
                UPDATE "UserSessions"
                SET "RefreshToken"  = 'rolled-back-' || replace("Id"::text, '-', ''),
                    "RevokedReason" = CASE WHEN NOT "IsRevoked" THEN 'Rollback-R2-DEV-03A' ELSE "RevokedReason" END,
                    "RevokedAt"     = CASE WHEN NOT "IsRevoked" THEN now() ELSE "RevokedAt" END,
                    "IsRevoked"     = TRUE;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_RefreshToken",
                table: "UserSessions",
                column: "RefreshToken",
                unique: true);
        }
    }
}
