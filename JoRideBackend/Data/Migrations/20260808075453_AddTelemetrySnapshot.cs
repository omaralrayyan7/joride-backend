using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JoRideBackend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetrySnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "telemetry_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    DeviceTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    Speed = table.Column<double>(type: "double precision", nullable: false),
                    VehicleId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telemetry_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_snapshots_DeviceId_DeviceTime",
                table: "telemetry_snapshots",
                columns: new[] { "DeviceId", "DeviceTime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_snapshots_VehicleId",
                table: "telemetry_snapshots",
                column: "VehicleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "telemetry_snapshots");
        }
    }
}
