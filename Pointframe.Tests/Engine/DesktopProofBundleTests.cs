using System.IO;
using System.Security.Cryptography;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopProofBundleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pointframe-bundle-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task WrittenReportVerifiesFromDisk()
    {
        var report = await SealedReportAsync();

        await DesktopProofBundle.WriteAsync(report, _directory);

        var json = await File.ReadAllTextAsync(Path.Combine(_directory, DesktopProofBundle.ReportFileName));
        var result = DesktopProofService.Verify(json, Path.Combine(_directory, DesktopProofBundle.EvidenceFolderName));
        Assert.True(result.IsValid, string.Join("; ", result.Problems));
        Assert.True(File.Exists(Path.Combine(_directory, DesktopProofBundle.IndexFileName)));
    }

    [Fact]
    public async Task IndexShowsCriteriaTimelineAndEvidence()
    {
        var html = DesktopProofBundle.RenderIndex(await SealedReportAsync());

        Assert.Contains("The <b>text</b> box shows hello", System.Net.WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("<b>text</b>", html);
        Assert.Contains("Action: enter_text", html);
        Assert.Contains("negative control", html);
        Assert.Contains("src=\"evidence/0001-check.png\"", html);
        Assert.Contains("No screenshot: NoVisibleWindow", html);
        Assert.Contains("is not itself signed", html);
    }

    [Fact]
    public void UnsignedReportSaysSo()
    {
        var html = DesktopProofBundle.RenderIndex(new DesktopTestReportService().Get("session-1"));

        Assert.Contains("This report is not signed.", html);
    }

    private async Task<DesktopTestReport> SealedReportAsync()
    {
        var evidenceDirectory = Path.Combine(_directory, DesktopProofBundle.EvidenceFolderName);
        Directory.CreateDirectory(evidenceDirectory);
        byte[] bytes = [1, 2, 3];
        await File.WriteAllBytesAsync(Path.Combine(evidenceDirectory, "0001-check.png"), bytes);

        var service = new DesktopTestReportService(signer: new Signer(_key));
        service.Initialize("session-1", "app.exe", "ABC", ["The <b>text</b> box shows hello"], evidenceDirectory, _directory);
        var actionId = Guid.NewGuid().ToString();
        service.RecordAction("session-1", new DesktopTestActionReport(
            actionId,
            DesktopTestReportService.UnannotatedActionDescription,
            new DesktopActionResult(
                DesktopTestingLimits.SchemaVersion,
                actionId,
                DesktopOperationStatus.Completed,
                DesktopDispatchStatus.Complete,
                DesktopVerificationStatus.NotRequested,
                DesktopObservationStatus.NotRequested),
            DateTimeOffset.UtcNow));
        service.AnnotateAction("session-1", actionId, "enter_text", new DesktopTestEvidence(null, null, null, DateTimeOffset.UtcNow, "NoVisibleWindow"));
        service.RecordCheck("session-1", new DesktopTestCheckReport(
            "textEquals", DesktopVerificationStatus.Passed, false, "server-uia", null, DateTimeOffset.UtcNow, "C1",
            Evidence: new DesktopTestEvidence("0001-check.png", Convert.ToHexString(SHA256.HashData(bytes)), null, DateTimeOffset.UtcNow)));
        service.RecordCheck("session-1", new DesktopTestCheckReport(
            "not textEquals", DesktopVerificationStatus.Passed, false, "server-uia", null, DateTimeOffset.UtcNow, NegativeControl: true));
        return await service.FinalizeAsync("session-1");
    }

    private sealed class Signer(ECDsa key) : IDesktopProofSigner
    {
        public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256);
    }
}
