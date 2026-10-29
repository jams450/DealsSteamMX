// Deterministic checks for the red bundle reminders (odd/tasks/bundle-expiry-acquisition.md, spec S2).
// Run with:
//   dotnet run --project checks/BundleAlerts.Checks
//
// No real ntfy message is sent and no database is touched: the bundle catalog, the acquisition table and the
// outbox are fakes, and the one HTTP client runs against a fake handler. A thrown Exception or a non-zero
// exit marks the suite failed.

BundleIntegrationChecks.Run();
BundleAlertPolicyChecks.Run();
await BundleAlertPipelineChecks.RunAsync();
await BundleAlertJobChecks.RunAsync();

Console.WriteLine("PASS: all bundle alert checks");
