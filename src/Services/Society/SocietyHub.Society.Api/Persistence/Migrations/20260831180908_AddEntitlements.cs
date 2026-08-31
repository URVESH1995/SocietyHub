using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocietyHub.Society.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_rollouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FeatureKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Stage = table.Column<int>(type: "int", nullable: false),
                    PilotSocietyIds = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Percentage = table.Column<int>(type: "int", nullable: false),
                    LastAdvancedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_rollouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "society_subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SocietyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Plan = table.Column<int>(type: "int", nullable: false),
                    PlanExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EnabledKeys = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DisabledKeys = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastChangeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_society_subscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Rollouts_Feature",
                table: "feature_rollouts",
                column: "FeatureKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Subscriptions_Society",
                table: "society_subscriptions",
                column: "SocietyId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feature_rollouts");

            migrationBuilder.DropTable(
                name: "society_subscriptions");
        }
    }
}
