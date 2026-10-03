using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pointframe.Engine.Automation.Services;

public sealed record DesktopProofEntry(
    string Kind,
    int Index,
    string Hash);

public sealed record DesktopProof(
    string Algorithm,
    IReadOnlyList<DesktopProofEntry> Entries,
    string RootHash,
    string KeyId,
    string PublicKey,
    string Signature);

public sealed record DesktopProofVerification(
    bool IsValid,
    string? KeyId,
    IReadOnlyList<string> Problems);

public interface IDesktopProofSigner
{
    byte[] PublicKey { get; }

    byte[] Sign(byte[] data);
}

// The private key is a persisted, non-exportable CNG key in the current user's key store. Anyone can check
// a signature against the public key in the report; the key ID says which Windows account's Pointframe
// made it. That is provenance, not correctness: it shows the report was not edited, not that it is right.
public sealed class CngDesktopProofSigner : IDesktopProofSigner, IDisposable
{
    public const string KeyName = "Pointframe.DesktopProof.v1";

    // Opened on first use rather than in the constructor: DI builds this with the report service, and a
    // key store failure there would take down every desktop tool instead of only the report that signs.
    private readonly Lazy<ECDsaCng> _key = new(OpenOrCreate);

    public byte[] PublicKey => _key.Value.ExportSubjectPublicKeyInfo();

    public byte[] Sign(byte[] data) => _key.Value.SignData(data, HashAlgorithmName.SHA256);

    public void Dispose()
    {
        if (_key.IsValueCreated)
        {
            _key.Value.Dispose();
        }
    }

    private static ECDsaCng OpenOrCreate()
    {
        var key = CngKey.Exists(KeyName)
            ? CngKey.Open(KeyName)
            : CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
            });
        return new ECDsaCng(key);
    }
}

public static class DesktopProofService
{
    public const string Algorithm = "sha256-chain+ecdsa-p256-sha256";

    // The canonical form every hash is computed over. Reading accepts any casing and enums as names or
    // numbers, so a report that went through the MCP serializer or was saved by hand still verifies.
    public static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static DesktopTestReport Seal(DesktopTestReport report, IDesktopProofSigner signer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(signer);
        var (entries, root) = ComputeChain(report);
        var publicKey = signer.PublicKey;
        return report with
        {
            Proof = new DesktopProof(
                Algorithm,
                entries,
                root,
                KeyIdFor(publicKey),
                Convert.ToBase64String(publicKey),
                Convert.ToBase64String(signer.Sign(Convert.FromHexString(root)))),
        };
    }

    public static DesktopProofVerification Verify(string reportJson, string? evidenceDirectory = null)
    {
        DesktopTestReport? report;
        try
        {
            report = JsonSerializer.Deserialize<DesktopTestReport>(reportJson, CanonicalJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return new DesktopProofVerification(false, null, [$"The report could not be read: {exception.Message}"]);
        }

        return report is null
            ? new DesktopProofVerification(false, null, ["The report is empty."])
            : Verify(report, evidenceDirectory);
    }

    public static DesktopProofVerification Verify(DesktopTestReport report, string? evidenceDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Proof is not { } proof)
        {
            return new DesktopProofVerification(false, null, ["The report has no proof."]);
        }

        var problems = new List<string>();
        if (proof.Algorithm != Algorithm)
        {
            problems.Add($"Unsupported proof algorithm '{proof.Algorithm}'.");
            return new DesktopProofVerification(false, proof.KeyId, problems);
        }

        // Naming the first entry that differs tells a reviewer what was edited, not only that something was.
        var (entries, root) = ComputeChain(report);
        if (entries.Count != proof.Entries.Count)
        {
            problems.Add($"The report has {entries.Count} entries but the proof covers {proof.Entries.Count}.");
        }

        var firstMismatch = entries.Zip(proof.Entries).FirstOrDefault(pair => pair.First != pair.Second);
        if (firstMismatch != default)
        {
            problems.Add($"Entry {firstMismatch.First.Kind}[{firstMismatch.First.Index}] does not match the proof.");
        }

        if (!string.Equals(root, proof.RootHash, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("The root hash does not match the report content.");
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(proof.PublicKey);
            if (KeyIdFor(publicKey) != proof.KeyId)
            {
                problems.Add("The key ID does not match the public key.");
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (!key.VerifyData(Convert.FromHexString(proof.RootHash), Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256))
            {
                problems.Add("The signature does not match the root hash.");
            }
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            problems.Add($"The signature could not be checked: {exception.Message}");
        }

        if (evidenceDirectory is not null)
        {
            problems.AddRange(VerifyEvidence(report, evidenceDirectory));
        }

        return new DesktopProofVerification(problems.Count == 0, proof.KeyId, problems);
    }

    public static string KeyIdFor(byte[] publicKey) =>
        Convert.ToHexString(SHA256.HashData(publicKey))[..16];

    private static (IReadOnlyList<DesktopProofEntry> Entries, string Root) ComputeChain(DesktopTestReport report)
    {
        var parts = new List<(string Kind, int Index, object Content)>
        {
            ("header", 0, new
            {
                report.SchemaVersion,
                report.SessionRef,
                report.ExecutablePath,
                report.ExecutableSha256,
                report.StartedUtc,
                report.CriteriaSha256,
                report.EvidenceDirectory,
                report.SessionDirectory,
            }),
        };
        parts.AddRange(report.Actions.Select((action, index) => ("action", index, (object)action)));
        parts.AddRange(report.Checks.Select((check, index) => ("check", index, (object)check)));
        parts.Add(("summary", 0, new { report.CompletedUtc, report.Verdict, report.Criteria }));

        var entries = new List<DesktopProofEntry>(parts.Count);
        var previous = new byte[32];
        foreach (var (kind, index, content) in parts)
        {
            var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, content.GetType(), CanonicalJson)));
            previous = SHA256.HashData([.. previous, .. contentHash]);
            entries.Add(new DesktopProofEntry(kind, index, Convert.ToHexString(previous)));
        }

        return (entries, Convert.ToHexString(previous));
    }

    private static IEnumerable<string> VerifyEvidence(DesktopTestReport report, string evidenceDirectory)
    {
        var evidence = report.Actions.Select(action => action.Evidence)
            .Concat(report.Checks.Select(check => check.Evidence))
            .Where(item => item?.Path is not null && item.Sha256 is not null);
        var root = Path.GetFullPath(evidenceDirectory);
        foreach (var item in evidence)
        {
            // The report is untrusted input here, so its paths may only name files inside the evidence folder.
            var path = Path.GetFullPath(Path.Combine(root, item!.Path!));
            if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"Evidence path {item.Path} points outside the evidence folder.";
                continue;
            }

            if (!File.Exists(path))
            {
                yield return $"Evidence file {item.Path} is missing.";
                continue;
            }

            if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"Evidence file {item.Path} does not match its hash.";
            }
        }
    }
}
