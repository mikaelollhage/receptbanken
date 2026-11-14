using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Receptbanken.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFamilyCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendarEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    StartTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FamilyMembersCsv = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ReminderMinutesBefore = table.Column<int>(type: "INTEGER", nullable: true),
                    RecurrenceIntervalWeeks = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    RecurrenceDaysOfWeekMask = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    RecurrenceEndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEvents", x => x.Id);
                    table.CheckConstraint("CK_CalendarEvents_AllDayTimes", "(IsAllDay = 1 AND StartTime IS NULL AND EndTime IS NULL) OR (IsAllDay = 0 AND StartTime IS NOT NULL)");
                    table.CheckConstraint("CK_CalendarEvents_RecurrenceIntervalWeeks", "RecurrenceIntervalWeeks >= 0 AND RecurrenceIntervalWeeks <= 52");
                });

            migrationBuilder.CreateTable(
                name: "CalendarEventExceptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CalendarEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsCancelled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    OverrideDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    OverrideTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OverrideIsAllDay = table.Column<bool>(type: "INTEGER", nullable: true),
                    OverrideStartTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    OverrideEndTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    OverrideLocation = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OverrideNotes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    OverrideFamilyMembersCsv = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    OverrideReminderMinutesBefore = table.Column<int>(type: "INTEGER", nullable: true),
                    OverrideReminderCleared = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventExceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarEventExceptions_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventExceptions_CalendarEventId_OriginalDate",
                table: "CalendarEventExceptions",
                columns: new[] { "CalendarEventId", "OriginalDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventExceptions_OverrideDate",
                table: "CalendarEventExceptions",
                column: "OverrideDate");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_RecurrenceIntervalWeeks",
                table: "CalendarEvents",
                column: "RecurrenceIntervalWeeks");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_StartDate_RecurrenceEndDate",
                table: "CalendarEvents",
                columns: new[] { "StartDate", "RecurrenceEndDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarEventExceptions");

            migrationBuilder.DropTable(
                name: "CalendarEvents");
        }
    }
}
