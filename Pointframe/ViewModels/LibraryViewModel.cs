using System.Collections.ObjectModel;
using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly ICaptureLibraryService _library;

    private CancellationTokenSource? _searchCts;

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

    public LibraryViewModel(ICaptureLibraryService library)
        => _library = library;

    public event Action<CaptureItem>? RequestOpen;

    public event Action? RequestClose;

    public ObservableCollection<CaptureItem> Captures { get; } = new();

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool HasFilters => HasSearchQuery || FromDate is not null || ToDate is not null;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Refresh()
    {
        // Supersede any in-flight search; OCR over a large library is slow and
        // each keystroke would otherwise queue another full pass.
        _searchCts?.Cancel();
        _searchCts?.Dispose();

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        IsSearching = true;
        ShowEmptyState = false;
        SearchStatus = null;

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
            }
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

    partial void OnSearchQueryChanged(string? value) => RefreshCommand.Execute(null);

    partial void OnFromUtcChanged(DateTime? value) => RefreshCommand.Execute(null);

    partial void OnToUtcChanged(DateTime? value) => RefreshCommand.Execute(null);

    // DatePicker yields a local calendar day; SearchAsync compares against UTC instants.
    // Map the picked day to its full local-day span so same-day captures are not excluded.
    partial void OnFromDateChanged(DateTime? value)
        => FromUtc = value?.Date.ToUniversalTime();

    partial void OnToDateChanged(DateTime? value)
        => ToUtc = value?.Date.AddDays(1).AddTicks(-1).ToUniversalTime();
}
