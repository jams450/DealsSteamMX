// Deterministic, no-database checks for the persisted discover catalog (spec S2/S3/S6, task T2) and the
// catalog price-freshness monitor (spec S4/S5/S6, task T2) plus its wishlist isolation (spec S4/S5). Run
// with:
//   dotnet run --project checks/CatalogDeals.Checks
//
// There is no test framework in this repo by design, so the assertions live in plain C# like the other
// console checks. Nothing here opens a connection: the catalog's SQL predicates are exercised through
// Expression.Compile over in-memory GameOffer/SteamGame rows, the selection/card rules through the pure
// policy the notifier already uses, and the wishlist-scoped services through the EF in-memory provider over
// the real Repository. The composed queries are additionally rendered with ToQueryString() against the Npgsql
// provider, which proves the production translation without connecting. What no check here can prove is that
// PostgreSQL returns the same rows: that gap is reported with the task.

CatalogDealChecks.Run();
CatalogFreshnessChecks.Run();
CatalogWishlistIsolationChecks.Run();
WishlistPriceObservedAtChecks.Run();
WishlistProviderRefreshChecks.Run();
CatalogAgeHoursChecks.Run();
