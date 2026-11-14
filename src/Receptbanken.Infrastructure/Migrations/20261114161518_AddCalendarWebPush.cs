using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Receptbanken.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarWebPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarReminderSystemStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    LastContentRestoredAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarReminderSystemStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Auth = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeviceLabel = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFailureAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CalendarReminderDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CalendarEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurrenceOriginalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    OccurrenceDisplayDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ScheduledTriggerUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PushSubscriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    NextAttemptAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LockedUntilUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LeaseToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SentAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastHttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarReminderDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarReminderDeliveries_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarReminderDeliveries_PushSubscriptions_PushSubscriptionId",
                        column: x => x.PushSubscriptionId,
                        principalTable: "PushSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarReminderDeliveries_CreatedAt",
                table: "CalendarReminderDeliveries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarReminderDeliveries_Expiry",
                table: "CalendarReminderDeliveries",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarReminderDeliveries_PushSubscriptionId",
                table: "CalendarReminderDeliveries",
                column: "PushSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarReminderDeliveries_UniqueOccurrenceDevice",
                table: "CalendarReminderDeliveries",
                columns: new[] { "CalendarEventId", "OccurrenceOriginalDate", "ScheduledTriggerUtc", "PushSubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarReminderDeliveries_WorkQueue",
                table: "CalendarReminderDeliveries",
                columns: new[] { "Status", "NextAttemptAtUtc", "LockedUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_IsActive",
                table: "PushSubscriptions",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarReminderDeliveries");

            migrationBuilder.DropTable(
                name: "CalendarReminderSystemStates");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");
        }
    }
}
