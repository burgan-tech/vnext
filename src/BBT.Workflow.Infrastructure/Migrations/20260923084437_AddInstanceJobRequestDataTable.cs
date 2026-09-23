using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Creates the <c>InstanceJobRequestData</c> table (one jsonb <c>Data</c> column, keyed by the
    /// job's id).
    ///
    /// <para>
    /// An oversized async transition body cannot travel in the Dapr job payload — the scheduler's
    /// transport (etcd) refuses messages over its ceiling (2 MiB by default) at arm time, after the
    /// Busy flip and the job row have committed, which left the instance durably Busy (finding
    /// AB-17, vnext-client-sdk-core#58). Such a body is persisted here at accept and the payload
    /// carries a reference; the job handler reads it back by id. A SEPARATE table, deliberately: the
    /// hot <c>InstanceJob</c> metadata reads (state-function scheduled-transition listing, updateData
    /// continuation handoff, cancellation) must never transfer a multi-MB body. Rows exist only for
    /// bodies above <c>WorkflowExecution:AsyncTransitionInlineBodyMaxBytes</c>; small bodies stay
    /// inline in the payload and create none.
    /// </para>
    /// <para>
    /// Downgrade note: a runtime from before this table ignores the payload's <c>DataInJobRow</c>
    /// flag and would run an offloaded transition with an empty body — drain async transition jobs
    /// before rolling back. Same window applies to a forward rolling deploy for bodies above the
    /// inline cap; keep the cap high enough that ordinary transitions stay inline.
    /// </para>
    /// </summary>
    public partial class AddInstanceJobRequestDataTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstanceJobRequestData",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceJobRequestData", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceJobRequestData",
                schema: "public");
        }
    }
}
