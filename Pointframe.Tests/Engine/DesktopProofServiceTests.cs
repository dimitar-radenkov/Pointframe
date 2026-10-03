using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopProofServiceTests : IDisposable
{
    private readonly EphemeralSigner _signer = new();
    private readonly string _evidence = Path.Combine(Path.GetTempPath(), $"pointframe-proof-{Guid.NewGuid():N}");

    public DesktopProofServiceTests()
    {
        Directory.CreateDirectory(_evidence);
    }

    public void Dispose()
    {
        _signer.Dispose();
        Directory.Delete(_evidence, recursive: true);
    }

    [Fact]
    public async Task SealedReportVerifies()
    {
        var report = await SealedReportAsync();

        var result = DesktopProofService.Verify(report, _evidence);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.Equal(DesktopProofService.KeyIdFor(_signer.PublicKey), result.KeyId);
        Assert.Equal(["header", "action", "check", "check", "summary"], report.Proof!.Entries.Select(entry => entry.Kind));
    }

    [Theory]
    [InlineData(JsonSerializerDefaults.Web)]
    [InlineData(JsonSerializerDefaults.General)]
    public async Task ReportStillVerifiesAfterAJsonRoundTrip(JsonSerializerDefaults defaults)
    {
        // The report reaches a verifier as JSON written by some other serializer: the MCP host's, or a file.
        var json = JsonSerializer.Serialize(await SealedReportAsync(), new JsonSerializerOptions(defaults) { WriteIndented = true });

        var result = DesktopProofService.Verify(json, _evidence);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
    }

    [Fact]
    public async Task ChangedVerdictIsDetected()
    {
        var report = await SealedReportAsync() with { Verdict = "passed" };

        var result = DesktopProofService.Verify(report);

        Assert.False(result.IsValid);
        Assert.Contains("Entry summary[0] does not match the proof.", result.Problems);
    }

    [Fact]
    public async Task ChangedCheckIsNamed()
    {
        var report = await SealedReportAsync();
        var checks = report.Checks.ToArray();
        checks[1] = checks[1] with { Verdict = DesktopVerificationStatus.Passed };

        var result = DesktopProofService.Verify(report with { Checks = checks });

        Assert.False(result.IsValid);
        Assert.Contains("Entry check[1] does not match the proof.", result.Problems);
    }

    [Fact]
    public async Task RemovedActionIsDetected()
    {
        var report = await SealedReportAsync();

        var result = DesktopProofService.Verify(report with { Actions = [] });

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Contains("entries but the proof covers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResealingWithAnotherKeyChangesTheKeyId()
    {
        // Anyone can re-sign an edited report with their own key; the key ID is what exposes it.
        var original = await SealedReportAsync();
        using var other = new EphemeralSigner();

        var resealed = DesktopProofService.Seal(original with { Verdict = "passed" }, other);
        var result = DesktopProofService.Verify(resealed);

        Assert.True(result.IsValid);
        Assert.NotEqual(original.Proof!.KeyId, result.KeyId);
    }

    [Fact]
    public async Task ForgedSignatureIsDetected()
    {
        var report = await SealedReportAsync();
        using var other = new EphemeralSigner();
        var forged = report.Proof! with { Signature = Convert.ToBase64String(other.Sign(Convert.FromHexString(report.Proof!.RootHash))) };

        var result = DesktopProofService.Verify(report with { Proof = forged });

        Assert.Contains("The signature does not match the root hash.", result.Problems);
    }

    [Fact]
    public async Task ChangedEvidenceFileIsDetected()
    {
        var report = await SealedReportAsync();
        await File.WriteAllBytesAsync(Path.Combine(_evidence, "0001-check.png"), [9, 9, 9]);

        var result = DesktopProofService.Verify(report, _evidence);

        Assert.Contains("Evidence file 0001-check.png does not match its hash.", result.Problems);
    }

    [Fact]
    public async Task EvidencePathOutsideTheFolderIsRejected()
    {
        var report = await SealedReportAsync();
        var checks = report.Checks.ToArray();
        checks[0] = checks[0] with { Evidence = checks[0].Evidence! with { Path = @"..\outside.png" } };
        var tampered = DesktopProofService.Seal(report with { Checks = checks }, _signer);

        var result = DesktopProofService.Verify(tampered, _evidence);

        Assert.Contains(@"Evidence path ..\outside.png points outside the evidence folder.", result.Problems);
    }

    [Fact]
    public void ReportWithoutProofIsNotValid()
    {
        var report = new DesktopTestReportService().Get("session-1");

        Assert.Contains("The report has no proof.", DesktopProofService.Verify(report).Problems);
    }

    [Fact]
    public void UnreadableJsonIsNotValid()
    {
        Assert.False(DesktopProofService.Verify("{ not json").IsValid);
    }

    private async Task<DesktopTestReport> SealedReportAsync()
    {
        var bytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(Path.Combine(_evidence, "0001-check.png"), bytes);
        var evidence = new DesktopTestEvidence("0001-check.png", Convert.ToHexString(SHA256.HashData(bytes)), new PixelBounds(0, 0, 10, 10), DateTimeOffset.UtcNow);

        var service = new DesktopTestReportService(signer: _signer);
        service.Initialize("session-1", "app.exe", "ABC", ["The text box shows hello"], _evidence);
        var actionId = Guid.NewGuid().ToString();
        service.RecordAction("session-1", new DesktopTestActionReport(
            actionId,
            "desktop action",
            new DesktopActionResult(
                DesktopTestingLimits.SchemaVersion,
                actionId,
                DesktopOperationStatus.Completed,
                DesktopDispatchStatus.Complete,
                DesktopVerificationStatus.NotRequested,
                DesktopObservationStatus.NotRequested,
                new DesktopOperationError("Example", "kept for the round trip")),
            DateTimeOffset.UtcNow));
        service.RecordCheck("session-1", new DesktopTestCheckReport(
            "textEquals", DesktopVerificationStatus.Passed, false, "server-uia", null, DateTimeOffset.UtcNow, "C1", Evidence: evidence));
        service.RecordCheck("session-1", new DesktopTestCheckReport(
            "not textEquals", DesktopVerificationStatus.Failed, false, "server-uia", "matched", DateTimeOffset.UtcNow, NegativeControl: true));
        return await service.FinalizeAsync("session-1");
    }

    private sealed class EphemeralSigner : IDesktopProofSigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);

        public void Dispose() => _key.Dispose();
    }
}
