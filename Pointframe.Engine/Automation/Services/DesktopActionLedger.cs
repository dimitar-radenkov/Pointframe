using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public sealed record DesktopActionLedgerEntry(
    string ActionId,
    string ArgumentsHash,
    DesktopActionResult Result,
    DateTimeOffset RecordedUtc);

public interface IDesktopActionLedger
{
    bool TryGet(string actionId, out DesktopActionLedgerEntry? entry);

    DesktopActionLedgerEntry Record(
        string actionId,
        string canonicalArguments,
        DesktopActionResult result);

    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed class DesktopActionLedger(TimeProvider? timeProvider = null) : IDesktopActionLedger
{
    private readonly ConcurrentDictionary<string, DesktopActionLedgerEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool TryGet(string actionId, out DesktopActionLedgerEntry? entry)
    {
        var found = _entries.TryGetValue(actionId, out var value);
        entry = value;
        return found;
    }

    public DesktopActionLedgerEntry Record(
        string actionId,
        string canonicalArguments,
        DesktopActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Guid.TryParse(actionId, out _))
        {
            throw new ArgumentException("ActionId must be a UUID.", nameof(actionId));
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalArguments ?? string.Empty)));
        var entry = new DesktopActionLedgerEntry(actionId, hash, result, _timeProvider.GetUtcNow());
        _entries.AddOrUpdate(actionId, entry, (_, _) => entry);
        return entry;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public static string Canonicalize(object? value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
    }
}
