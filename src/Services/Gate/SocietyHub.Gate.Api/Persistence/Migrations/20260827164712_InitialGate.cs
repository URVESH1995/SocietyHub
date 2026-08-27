using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocietyHub.Gate.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttendanceRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DailyHelpId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FirstInAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastOutAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PunchCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlacklistEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PersonName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewDueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LiftedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LiftedReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlacklistEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyHelps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BadgeCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PhotoBlobKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyHelps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GateEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PartitionKey = table.Column<int>(type: "int", nullable: false),
                    VisitPassId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DailyHelpId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FlatId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PersonName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PersonPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    VisitorType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    VehicleNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PhotoBlobKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RecordedByGuardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedOnDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WasOfflineCapture = table.Column<bool>(type: "bit", nullable: false),
                    LeftAtGate = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsumerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.EventId, x.ConsumerName });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsPoisoned = table.Column<bool>(type: "bit", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SosIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RaisedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlatId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RaisedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcknowledgedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SosIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VisitPasses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlatId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorisedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    VisitorPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    VisitorType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ValidFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CodeSalt = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    VerificationAttempts = table.Column<int>(type: "int", nullable: false),
                    CheckedInAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CheckedOutAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CheckedInByGuardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VehicleNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PhotoBlobKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ExpectedPersonCount = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitPasses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HelpAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DailyHelpId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlatId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpAssignments_DailyHelps_DailyHelpId",
                        column: x => x.DailyHelpId,
                        principalTable: "DailyHelps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attendance_Help_Date",
                table: "AttendanceRecords",
                columns: new[] { "DailyHelpId", "WorkDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendance_Society_Date",
                table: "AttendanceRecords",
                columns: new[] { "SocietyId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Blacklist_Society_Phone",
                table: "BlacklistEntries",
                columns: new[] { "SocietyId", "PhoneNumber", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyHelps_Badge",
                table: "DailyHelps",
                columns: new[] { "SocietyId", "BadgeCode" },
                unique: true,
                filter: "[BadgeCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DailyHelps_Phone",
                table: "DailyHelps",
                columns: new[] { "SocietyId", "PhoneNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_GateEntries_Flat_Time",
                table: "GateEntries",
                columns: new[] { "FlatId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GateEntries_Partition_Society_Time",
                table: "GateEntries",
                columns: new[] { "PartitionKey", "SocietyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GateEntries_Pass",
                table: "GateEntries",
                column: "VisitPassId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpAssignments_DailyHelpId",
                table: "HelpAssignments",
                column: "DailyHelpId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpAssignments_Flat",
                table: "HelpAssignments",
                columns: new[] { "FlatId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ReceivedAtUtc",
                table: "InboxMessages",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages",
                columns: new[] { "NextAttemptAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SocietyId_IsPoisoned",
                table: "OutboxMessages",
                columns: new[] { "SocietyId", "IsPoisoned" });

            migrationBuilder.CreateIndex(
                name: "IX_Sos_Society_Status",
                table: "SosIncidents",
                columns: new[] { "SocietyId", "Status", "RaisedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitPasses_Flat_Window",
                table: "VisitPasses",
                columns: new[] { "FlatId", "ValidFromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitPasses_Open",
                table: "VisitPasses",
                columns: new[] { "SocietyId", "Status", "ValidUntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceRecords");

            migrationBuilder.DropTable(
                name: "BlacklistEntries");

            migrationBuilder.DropTable(
                name: "GateEntries");

            migrationBuilder.DropTable(
                name: "HelpAssignments");

            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "SosIncidents");

            migrationBuilder.DropTable(
                name: "VisitPasses");

            migrationBuilder.DropTable(
                name: "DailyHelps");
        }
    }
}
