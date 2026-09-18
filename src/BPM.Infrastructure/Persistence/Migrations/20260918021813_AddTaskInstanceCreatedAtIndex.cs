using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BPM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskInstanceCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_CreatedAt",
                table: "TaskInstances",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TaskInstances_CreatedAt",
                table: "TaskInstances");
        }
    }
}
