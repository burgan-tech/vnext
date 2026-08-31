using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BBT.Workflow.Migrations
{
    /// <summary>
    /// Adds a partial GIN index (<c>jsonb_path_ops</c>, <c>IsLatest = true</c>) on
    /// <c>InstancesData.Data</c>. Attribute equality filters already emit
    /// <c>"Data" @&gt; {param}</c> containment predicates joined to the latest data row
    /// (see <c>GraphQLJsonFilterService.BuildEqualsCondition</c>); without this index every
    /// such filter is a sequential scan over the whole data table. History rows are excluded
    /// to keep the index small and its write amplification bounded.
    /// </summary>
    public partial class AddInstancesDataGinIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InstancesData_Data_Gin",
                schema: "public",
                table: "InstancesData",
                column: "Data",
                filter: "\"IsLatest\" = true")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InstancesData_Data_Gin",
                schema: "public",
                table: "InstancesData");
        }
    }
}
