using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BPM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessInstanceBusinessKeyUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ProcessInstances_TenantId_ProcessDefinitionId_BusinessKey",
                table: "ProcessInstances",
                columns: new[] { "TenantId", "ProcessDefinitionId", "BusinessKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessInstances_TenantId_ProcessDefinitionId_BusinessKey",
                table: "ProcessInstances");
        }
    }
}
