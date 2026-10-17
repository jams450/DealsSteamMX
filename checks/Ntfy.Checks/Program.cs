// Deterministic checks for the ntfy deal alerts. Run with:
//   dotnet run --project checks/Ntfy.Checks
// No real ntfy message is ever sent: the HTTP sender runs against a fake handler and every other seam is
// a fake too. A non-zero exit or a thrown Exception marks the check suite failed.

NtfyPolicyChecks.Run();
await NtfySenderChecks.RunAsync();
await NtfyDispatcherChecks.RunAsync();

Console.WriteLine("PASS: all ntfy checks");
