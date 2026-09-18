using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BPM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangeReason",
                table: "ProcessVersions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "ProcessDefinitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessDefinitions_OwnerUserId",
                table: "ProcessDefinitions",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProcessDefinitions_Users_OwnerUserId",
                table: "ProcessDefinitions",
                column: "OwnerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProcessDefinitions_Users_OwnerUserId",
                table: "ProcessDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_ProcessDefinitions_OwnerUserId",
                table: "ProcessDefinitions");

            migrationBuilder.DropColumn(
                name: "ChangeReason",
                table: "ProcessVersions");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "ProcessDefinitions");
        }
    }
}
