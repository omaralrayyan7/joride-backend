using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JoRideBackend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessedPaymentEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "processed_payment_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderEventId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_payment_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_processed_payment_events_ProviderEventId",
                table: "processed_payment_events",
                column: "ProviderEventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_payment_events");
        }
    }
}
