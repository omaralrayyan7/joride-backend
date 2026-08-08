using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JoRideBackend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentAdminE54 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_admin_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PaymentIntentId = table.Column<Guid>(type: "uuid", nullable: true),
                    PendingTopUpId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdminUserId = table.Column<int>(type: "integer", nullable: false),
                    AdminLabel = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_admin_audits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pending_topups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResolvedByAdminUserId = table.Column<int>(type: "integer", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_topups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_admin_audits_PaymentIntentId",
                table: "payment_admin_audits",
                column: "PaymentIntentId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_admin_audits_PendingTopUpId",
                table: "payment_admin_audits",
                column: "PendingTopUpId");

            migrationBuilder.CreateIndex(
                name: "IX_pending_topups_Status",
                table: "pending_topups",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_pending_topups_UserId",
                table: "pending_topups",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_admin_audits");

            migrationBuilder.DropTable(
                name: "pending_topups");
        }
    }
}
