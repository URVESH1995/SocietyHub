using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocietyHub.Notice.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "notices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Audience = table.Column<int>(type: "int", nullable: false),
                    TargetTowers = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TargetFlatIds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TitleEn = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BodyEn = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    TitleHi = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    BodyHi = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    RequiresAcknowledgement = table.Column<bool>(type: "bit", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notices", x => x.Id);
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
                name: "polls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    QuestionEn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    QuestionHi = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NoticeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultsHiddenUntilClose = table.Column<bool>(type: "bit", nullable: false),
                    QuorumPercent = table.Column<int>(type: "int", nullable: false),
                    EligibleFlatCount = table.Column<int>(type: "int", nullable: false),
                    OpensAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosesAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_polls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notice_acknowledgements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NoticeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notice_acknowledgements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notice_acknowledgements_notices_NoticeId",
                        column: x => x.NoticeId,
                        principalTable: "notices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "poll_options",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PollId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    LabelEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LabelHi = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_options", x => x.Id);
                    table.ForeignKey(
                        name: "FK_poll_options_polls_PollId",
                        column: x => x.PollId,
                        principalTable: "polls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "poll_votes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PollId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlatId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoterUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OptionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CastAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ChangeCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poll_votes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_poll_votes_polls_PollId",
                        column: x => x.PollId,
                        principalTable: "polls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ReceivedAtUtc",
                table: "InboxMessages",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "ux_notice_ack_user",
                table: "notice_acknowledgements",
                columns: new[] { "NoticeId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notices_expiry",
                table: "notices",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_notices_feed",
                table: "notices",
                columns: new[] { "SocietyId", "Status", "IsPinned", "PublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages",
                columns: new[] { "NextAttemptAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SocietyId_IsPoisoned",
                table: "OutboxMessages",
                columns: new[] { "SocietyId", "IsPoisoned" });

            migrationBuilder.CreateIndex(
                name: "IX_poll_options_PollId",
                table: "poll_options",
                column: "PollId");

            migrationBuilder.CreateIndex(
                name: "ux_poll_vote_flat",
                table: "poll_votes",
                columns: new[] { "PollId", "FlatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_polls_open",
                table: "polls",
                columns: new[] { "SocietyId", "Status", "ClosesAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "notice_acknowledgements");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "poll_options");

            migrationBuilder.DropTable(
                name: "poll_votes");

            migrationBuilder.DropTable(
                name: "notices");

            migrationBuilder.DropTable(
                name: "polls");
        }
    }
}
