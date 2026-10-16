# WPF Contacts sample

A tiny Windows WPF app for adding contacts. Build and run with `dotnet run --project WpfSample.csproj` (requires the .NET 10 Windows desktop targeting pack).

Build the deliberate broken variant with `dotnet build WpfSample.csproj -p:SampleBroken=true`; it updates the status but does not add the contact to the list.

This sample is a cold-agent target for Pointframe verification trials. Its reference spec is kept in `reference/` so trial copies start without a verification spec.
