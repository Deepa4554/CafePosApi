using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CafePOS.Api.Migrations
{
    /// <summary>
    /// Deliberately empty — this migration exists only to carry a corrected model
    /// snapshot, not to change the database.
    ///
    /// The aashish merge resolved CafePosDbContextModelSnapshot.cs by taking the
    /// pre-Offers version, so the snapshot lost the Offer/OfferMenuItem entities and
    /// the Offer columns on Orders/OrderItems while the DbContext kept them. EF Core 9
    /// compares the runtime model against the last migration's snapshot on
    /// Database.Migrate(), saw the difference, and threw PendingModelChangesWarning at
    /// startup — the app crashed before serving a request.
    ///
    /// The schema itself was never wrong: 20260811040447_AddOffers already creates
    /// those tables and columns. So Up/Down do nothing; regenerating the snapshot
    /// alongside them is the whole point. Re-running the DDL here would fail against
    /// any database that already has AddOffers applied.
    /// </summary>
    public partial class SyncOffersModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
