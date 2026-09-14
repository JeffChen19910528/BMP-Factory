using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BPM.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublishedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessVersions_ProcessDefinitions_ProcessDefinitionId",
                        column: x => x.ProcessDefinitionId,
                        principalTable: "ProcessDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcessInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InitiatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessInstances_ProcessDefinitions_ProcessDefinitionId",
                        column: x => x.ProcessDefinitionId,
                        principalTable: "ProcessDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcessInstances_ProcessVersions_ProcessVersionId",
                        column: x => x.ProcessVersionId,
                        principalTable: "ProcessVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NodeName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssigneeRole = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskInstances_ProcessInstances_ProcessInstanceId",
                        column: x => x.ProcessInstanceId,
                        principalTable: "ProcessInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessDefinitions_TenantId_Key",
                table: "ProcessDefinitions",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessInstances_InitiatorId",
                table: "ProcessInstances",
                column: "InitiatorId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessInstances_ProcessDefinitionId",
                table: "ProcessInstances",
                column: "ProcessDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessInstances_ProcessVersionId",
                table: "ProcessInstances",
                column: "ProcessVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessInstances_Status",
                table: "ProcessInstances",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessVersions_ProcessDefinitionId_VersionNumber",
                table: "ProcessVersions",
                columns: new[] { "ProcessDefinitionId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_AssigneeId",
                table: "TaskInstances",
                column: "AssigneeId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_DueAt",
                table: "TaskInstances",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_ProcessInstanceId",
                table: "TaskInstances",
                column: "ProcessInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstances_Status",
                table: "TaskInstances",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskInstances");

            migrationBuilder.DropTable(
                name: "ProcessInstances");

            migrationBuilder.DropTable(
                name: "ProcessVersions");

            migrationBuilder.DropTable(
                name: "ProcessDefinitions");
        }
    }
}
