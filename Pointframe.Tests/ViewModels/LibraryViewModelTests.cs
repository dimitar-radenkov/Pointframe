using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class LibraryViewModelTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RefreshCommand_LoadsCapturesFromService()
    {
        var library = LibraryReturning(Item("a.png", T1), Item("b.png", T2));
        var sut = new LibraryViewModel(library.Object);

        await sut.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, sut.Captures.Count);
        Assert.Equal("a.png", sut.Captures[0].FileName);
    }

    [Fact]
    public async Task SearchQuery_Changed_RequeriesWithQuery()
    {
        var library = LibraryReturning();
        var sut = new LibraryViewModel(library.Object);

        sut.SearchQuery = "shot";
        await WaitForRefresh(sut);

        library.Verify(
            l => l.SearchAsync("shot", null, null, It.IsAny<IProgress<CaptureSearchProgress>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DateFilters_Changed_RequeryWithBounds()
    {
        var library = LibraryReturning();
        var sut = new LibraryViewModel(library.Object);

        sut.FromUtc = T1;
        await WaitForRefresh(sut);
        sut.ToUtc = T2;
        await WaitForRefresh(sut);

        library.Verify(
            l => l.SearchAsync(null, T1, T2, It.IsAny<IProgress<CaptureSearchProgress>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RefreshCommand_RepopulatesCaptures()
    {
        var library = new Mock<ICaptureLibraryService>();
        library.SetupSequence(l => l.SearchAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IProgress<CaptureSearchProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Item("a.png", T1) })
            .ReturnsAsync(new[] { Item("b.png", T2), Item("c.png", T2) });
        var sut = new LibraryViewModel(library.Object);

        await sut.RefreshCommand.ExecuteAsync(null);
        await sut.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, sut.Captures.Count);
        Assert.Equal("b.png", sut.Captures[0].FileName);
    }

    [Fact]
    public async Task IsSearching_IsClearedAfterSearchCompletes()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);

        await sut.RefreshCommand.ExecuteAsync(null);

        Assert.False(sut.IsSearching);
    }

    [Fact]
    public void FromDate_MapsToStartOfDayUtc()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);
        var picked = new DateTime(2026, 3, 5, 13, 45, 0, DateTimeKind.Unspecified);

        sut.FromDate = picked;

        Assert.Equal(picked.Date.ToUniversalTime(), sut.FromUtc);
    }

    [Fact]
    public void ToDate_MapsToEndOfDayUtc_SoSameDayCapturesStayIncluded()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);
        var picked = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Unspecified);

        sut.ToDate = picked;

        var lateThatDay = new DateTime(2026, 3, 5, 23, 30, 0, DateTimeKind.Unspecified).ToUniversalTime();
        Assert.Equal(picked.Date.AddDays(1).AddTicks(-1).ToUniversalTime(), sut.ToUtc);
        Assert.True(lateThatDay <= sut.ToUtc);
    }

    [Fact]
    public void OpenCommand_WithSelection_RaisesRequestOpen()
    {
        var item = Item("a.png", T1);
        var sut = new LibraryViewModel(LibraryReturning(item).Object);
        CaptureItem? opened = null;
        sut.RequestOpen += capture => opened = capture;

        sut.SelectedItem = item;
        sut.OpenCommand.Execute(null);

        Assert.Same(item, opened);
    }

    [Fact]
    public void OpenCommand_WithoutSelection_DoesNothing()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);
        var raised = false;
        sut.RequestOpen += _ => raised = true;

        sut.OpenCommand.Execute(null);

        Assert.False(raised);
    }

    [Fact]
    public async Task ShowEmptyState_IsSetWhenSearchReturnsNothing()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);

        await sut.RefreshCommand.ExecuteAsync(null);

        Assert.True(sut.ShowEmptyState);
        Assert.Equal(0, sut.ResultCount);
    }

    [Fact]
    public async Task ShowEmptyState_IsClearedWhenResultsExist()
    {
        var sut = new LibraryViewModel(LibraryReturning(Item("a.png", T1)).Object);

        await sut.RefreshCommand.ExecuteAsync(null);

        Assert.False(sut.ShowEmptyState);
        Assert.Equal(1, sut.ResultCount);
    }

    [Fact]
    public async Task RefreshCommand_TracksOcrSearchUsageTelemetry_WhenQueryMeetsThreshold()
    {
        var library = LibraryReturning(Item("a.png", T1));
        var telemetry = new Mock<ITelemetryService>();
        var sut = new LibraryViewModel(library.Object, ImmediateDebounceService.Instance, telemetry.Object)
        {
            SearchQuery = "invoice"
        };

        await WaitForRefresh(sut);

        telemetry.Verify(
            t => t.TrackEvent("library_ocr_search_used", It.IsAny<IReadOnlyDictionary<string, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RefreshCommand_DoesNotTrackOcrSearchUsageTelemetry_WhenQueryBelowThreshold()
    {
        var library = LibraryReturning(Item("a.png", T1));
        var telemetry = new Mock<ITelemetryService>();
        var sut = new LibraryViewModel(library.Object, ImmediateDebounceService.Instance, telemetry.Object)
        {
            SearchQuery = "ab"
        };

        await WaitForRefresh(sut);

        telemetry.Verify(
            t => t.TrackEvent("library_ocr_search_used", It.IsAny<IReadOnlyDictionary<string, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task ClearSearchCommand_ResetsQueryAndRequeries()
    {
        var library = LibraryReturning();
        var sut = new LibraryViewModel(library.Object);
        sut.SearchQuery = "shot";
        await WaitForRefresh(sut);

        sut.ClearSearchCommand.Execute(null);
        await WaitForRefresh(sut);

        Assert.Null(sut.SearchQuery);
        Assert.False(sut.HasSearchQuery);
        library.Verify(
            l => l.SearchAsync(null, null, null, It.IsAny<IProgress<CaptureSearchProgress>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ClearFiltersCommand_ResetsQueryAndDates()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);
        sut.SearchQuery = "shot";
        sut.FromDate = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Unspecified);
        sut.ToDate = new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Unspecified);
        await WaitForRefresh(sut);

        sut.ClearFiltersCommand.Execute(null);
        await WaitForRefresh(sut);

        Assert.Null(sut.SearchQuery);
        Assert.Null(sut.FromDate);
        Assert.Null(sut.ToDate);
        Assert.Null(sut.FromUtc);
        Assert.Null(sut.ToUtc);
        Assert.False(sut.HasFilters);
    }

    [Fact]
    public void HasFilters_TracksQueryAndDates()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);
        Assert.False(sut.HasFilters);

        sut.FromDate = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.True(sut.HasFilters);
    }

    // The getter being right is not enough — the UI only reacts to the notification.
    [Fact]
    public void SearchQuery_Changed_NotifiesDependentFlags()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);
        var notified = new List<string?>();
        sut.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        sut.SearchQuery = "shot";

        Assert.Contains(nameof(LibraryViewModel.HasSearchQuery), notified);
        Assert.Contains(nameof(LibraryViewModel.HasFilters), notified);
        Assert.True(sut.HasFilters);
    }

    [Fact]
    public void FromDate_Changed_NotifiesHasFilters()
    {
        var sut = new LibraryViewModel(LibraryReturning().Object);
        var notified = new List<string?>();
        sut.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        sut.FromDate = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.Contains(nameof(LibraryViewModel.HasFilters), notified);
    }

    private static async Task WaitForRefresh(LibraryViewModel sut)
    {
        var task = sut.RefreshCommand.ExecutionTask;
        if (task is not null)
        {
            await task;
        }
    }

    private static Mock<ICaptureLibraryService> LibraryReturning(params CaptureItem[] results)
    {
        var library = new Mock<ICaptureLibraryService>();
        library
            .Setup(l => l.SearchAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<IProgress<CaptureSearchProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);
        return library;
    }

    private static CaptureItem Item(string name, DateTime capturedUtc)
        => new(name, name, capturedUtc);
}
