// Deterministic, no-database checks for the persisted discover catalog (spec S2/S3/S6, task T2).
// Run with:
//   dotnet run --project checks/CatalogDeals.Checks
//
// There is no test framework in this repo by design, so the assertions live in plain C# like the other
// console checks. Nothing here opens a connection: the catalog's SQL predicates are exercised through
// Expression.Compile over in-memory GameOffer/SteamGame rows, and the selection/card rules through the
// pure policy the notifier already uses. The EF translation of the composed query is NOT covered here
// (and cannot be: the checks are forbidden a database); that gap is reported with the task.

CatalogDealChecks.Run();
