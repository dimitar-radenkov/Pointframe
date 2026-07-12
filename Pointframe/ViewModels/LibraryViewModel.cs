using System.Collections.ObjectModel;
using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private const int SearchDebounceMilliseconds = 300;

    private readonly ICaptureLibraryService _library;
    private readonly IDebounceService _debounceService;
    private readonly ITelemetryService _telemetry;
    private readonly LibraryDateRangeOption _allTimeOption = new("All time", null);
    private readonly LibraryDateRangeOption _customOption = new("Custom", null);
    private readonly string _searchDebounceKey = $"{nameof(LibraryViewModel)}.Search.{Guid.NewGuid():N}";

    private CancellationTokenSource? _searchCts;
    private bool _isApplyingPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    private string? _searchQuery;

    [ObservableProperty]
    private DateTime? _fromUtc;

    [ObservableProperty]
    private DateTime? _toUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    private DateTime? _fromDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    private DateTime? _toDate;

    [ObservableProperty]
    private CaptureItem? _selectedItem;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string? _searchStatus;

    [ObservableProperty]
    private bool _showEmptyState;

    [ObservableProperty]
    private int _resultCount;

    [ObservableProperty]
    private LibraryDateRangeOption? _selectedDateRangeOption;

    public LibraryViewModel(ICaptureLibraryService library)
        : this(library, ImmediateDebounceService.Instance, NullTelemetryService.Instance)
    {
    }

    public LibraryViewModel(ICaptureLibraryService library, IDebounceService debounceService)
        : this(library, debounceService, NullTelemetryService.Instance)
    {
    }

    public LibraryViewModel(
        ICaptureLibraryService library,
        IDebounceService debounceService,
        ITelemetryService telemetry)
    {
        _library = library;
        _debounceService = debounceService;
        _telemetry = telemetry;
        DateRangeOptions =
        [
            _allTimeOption,
            new LibraryDateRangeOption("Last 7 days", 7),
            new LibraryDateRangeOption("Last 30 days", 30),
            new LibraryDateRangeOption("Last 90 days", 90),
            _customOption,
        ];
        SelectedDateRangeOption = _allTimeOption;
    }

    public event Action<CaptureItem>? RequestOpen;

    public event Action? RequestClose;

    public ObservableCollection<CaptureItem> Captures { get; } = new();

    public IReadOnlyList<LibraryDateRangeOption> DateRangeOptions { get; }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool HasFilters => HasSearchQuery || FromDate is not null || ToDate is not null;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Refresh()
    {
        // Supersede any in-flight search; OCR over a large library is slow and
        // each keystroke would otherwise queue another full pass.
        var cts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _searchCts, cts);
        previousCts?.Cancel();

        IsSearching = true;
        ShowEmptyState = false;
        SearchStatus = null;
        var normalizedQuery = SearchQuery?.Trim();
        var queryLength = normalizedQuery?.Length ?? 0;
        var isOcrEligibleQuery = queryLength >= 3;

        var progress = new Progress<CaptureSearchProgress>(report =>
        {
            if (ReferenceEquals(_searchCts, cts) && report.Scanned < report.Total)
            {
                SearchStatus = $"Searching image text… {report.Scanned} of {report.Total}";
            }
        });

        try
        {
            var results = await _library.SearchAsync(SearchQuery, FromUtc, ToUtc, progress, cts.Token);

            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            Captures.Clear();

            foreach (var item in results)
            {
                Captures.Add(item);
            }

            ResultCount = Captures.Count;
            ShowEmptyState = Captures.Count == 0;

            if (isOcrEligibleQuery)
            {
                _telemetry.TrackEvent("library_ocr_search_used");
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
                SearchStatus = null;
                _searchCts = null;
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = null;

    [RelayCommand]
    private void ClearFilters()
    {
        SearchQuery = null;
        FromDate = null;
        ToDate = null;
        SelectedDateRangeOption = _allTimeOption;
    }

    [RelayCommand]
    private void Open()
    {
        if (SelectedItem is not null)
        {
            RequestOpen?.Invoke(SelectedItem);
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    partial void OnSearchQueryChanged(string? value)
    {
        _ = _debounceService.DebounceAsync(
            _searchDebounceKey,
            TimeSpan.FromMilliseconds(SearchDebounceMilliseconds),
            _ =>
            {
                RefreshCommand.Execute(null);
                return Task.CompletedTask;
            });
    }

    partial void OnFromUtcChanged(DateTime? value) => RefreshCommand.Execute(null);

    partial void OnToUtcChanged(DateTime? value) => RefreshCommand.Execute(null);

    // DatePicker yields a local calendar day; SearchAsync compares against UTC instants.
    // Map the picked day to its full local-day span so same-day captures are not excluded.
    partial void OnFromDateChanged(DateTime? value)
    {
        FromUtc = value?.Date.ToUniversalTime();
        EnsureCustomPreset();
    }

    partial void OnToDateChanged(DateTime? value)
    {
        ToUtc = value?.Date.AddDays(1).AddTicks(-1).ToUniversalTime();
        EnsureCustomPreset();
    }

    partial void OnSelectedDateRangeOptionChanged(LibraryDateRangeOption? value)
    {
        if (value is null || value == _customOption)
        {
            return;
        }

        _isApplyingPreset = true;
        try
        {
            if (value == _allTimeOption)
            {
                FromDate = null;
                ToDate = null;
                return;
            }

            if (value.Days is int days)
            {
                var today = DateTime.Today;
                ToDate = today;
                FromDate = today.AddDays(-(days - 1));
            }
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }

    private void EnsureCustomPreset()
    {
        if (_isApplyingPreset)
        {
            return;
        }

        if (FromDate is null && ToDate is null)
        {
            if (SelectedDateRangeOption != _allTimeOption)
            {
                SelectedDateRangeOption = _allTimeOption;
            }

            return;
        }

        if (SelectedDateRangeOption != _customOption)
        {
            SelectedDateRangeOption = _customOption;
        }
    }

}

public sealed record LibraryDateRangeOption(string Label, int? Days);

internal sealed class NullTelemetryService : ITelemetryService
{
    public static NullTelemetryService Instance { get; } = new();

    private NullTelemetryService()
    {
    }

    public void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
    }

    public void TrackException(Exception exception, string? context = null)
    {
    }

    public void Flush()
    {
    }
}
