using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using Wpf.Ui.Contracts;
using XAU.Services;

namespace XAU.ViewModels.Pages;

public partial class AchievementResearchLabViewModel : ObservableObject, INavigationAware
{
    [ObservableProperty] private string _eventTokenStatus = "Events token status has not been checked.";
    [ObservableProperty] private string _achievementStatus = "Select an event-based title to begin research.";
    [ObservableProperty] private string _titleSearchText = "";
    [ObservableProperty] private string _titleListStatus = "Loading event-based research titles...";
    [ObservableProperty] private ObservableCollection<ResearchTitleCard> _researchTitles = new();
    [ObservableProperty] private ObservableCollection<ResearchTitleCard> _filteredResearchTitles = new();
    [ObservableProperty] private ResearchTitleCard? _selectedResearchTitle;
    [ObservableProperty] private string _eventTemplateInfo = "Select a title to inspect its event template.";
    [ObservableProperty] private bool _canUseProgressionDataTemplate;
    [ObservableProperty] private ObservableCollection<ResearchAchievement> _achievements = new();
    [ObservableProperty] private ResearchAchievement? _selectedAchievement;
    [ObservableProperty] private string _progressionData = "";
    [ObservableProperty] private string _payloadPreview = "";
    [ObservableProperty] private string _testStatus = "Select an achievement and preview one ProgressionData candidate.";
    [ObservableProperty] private string _resultDetails = "";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string _rangeStart = "1";
    [ObservableProperty] private string _rangeEnd = "10";
    [ObservableProperty] private string _rangeDelaySeconds = "10";
    [ObservableProperty] private string _rangeMaxAttempts = "10";
    [ObservableProperty] private bool _stopOnAchievementUnlocked = true;
    [ObservableProperty] private bool _stopOnStateChanged = true;
    [ObservableProperty] private bool _stopOnRequirementChanged = true;
    [ObservableProperty] private bool _stopOnSelectedAchievementChange = true;
    [ObservableProperty] private bool _stopOnAnyAchievementChange = true;
    [ObservableProperty] private bool _watchSelectedAchievementOnly;
    [ObservableProperty] private bool _watchAllAchievements = true;
    [ObservableProperty] private bool _isRangeTesting;
    [ObservableProperty] private string _rangeStatus = "Range tester is idle.";
    [ObservableProperty] private string _rangeCurrentStatus = "No candidate is running.";
    [ObservableProperty] private string _proposedMapping = "";
    [ObservableProperty] private ObservableCollection<ResearchRangeAttemptRow> _rangeAttempts = new();
    [ObservableProperty] private string _candidateHistorySummary = "No tested-candidate summary loaded.";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _historyStatus = "No mapping research history loaded.";
    [ObservableProperty] private ObservableCollection<AchievementMappingResearchEntry> _entries = new();
    [ObservableProperty] private AchievementMappingResearchEntry? _selectedEntry;
    [ObservableProperty] private ObservableCollection<ResearchRequirementAnalysisRow> _requirementAnalysisRows = new();
    [ObservableProperty] private string _requirementAnalysisSummary = "Load a title's achievements to analyse requirement structures.";

    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _rangeCancellation;
    private IReadOnlyList<AchievementMappingResearchEntry> _allHistoryEntries = [];
    private JObject? _eventData;

    public AchievementResearchLabViewModel(
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
    }

    public string TitleInfo => SelectedResearchTitle == null
        ? "Achievement Mapping Research Lab"
        : $"{SelectedResearchTitle.Name} | Title ID {SelectedResearchTitle.TitleId} | SCID {SelectedResearchTitle.Scid}";
    public string HistoryPath => SelectedResearchTitle == null
        ? "<select a title>"
        : AchievementMappingResearchStore.FilePath(SelectedResearchTitle.TitleId);

    public async void OnNavigatedTo()
    {
        UpdateEventTokenStatus();
        await LoadResearchTitles();
    }

    public void OnNavigatedFrom() => _rangeCancellation?.Cancel();

    partial void OnSearchTextChanged(string value) => LoadHistory();

    partial void OnTitleSearchTextChanged(string value) => FilterResearchTitles();

    partial void OnCanUseProgressionDataTemplateChanged(bool value)
    {
        SendOneTestEventCommand.NotifyCanExecuteChanged();
        StartRangeTestCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSendingChanged(bool value)
    {
        SendOneTestEventCommand.NotifyCanExecuteChanged();
        RefreshAchievementsCommand.NotifyCanExecuteChanged();
        StartRangeTestCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRangeTestingChanged(bool value)
    {
        SendOneTestEventCommand.NotifyCanExecuteChanged();
        RefreshAchievementsCommand.NotifyCanExecuteChanged();
        StartRangeTestCommand.NotifyCanExecuteChanged();
        StopRangeTestCommand.NotifyCanExecuteChanged();
    }

    partial void OnWatchSelectedAchievementOnlyChanged(bool value)
    {
        if (value)
            WatchAllAchievements = false;
        else if (!WatchAllAchievements)
            WatchAllAchievements = true;
    }

    partial void OnWatchAllAchievementsChanged(bool value)
    {
        if (value)
            WatchSelectedAchievementOnly = false;
        else if (!WatchSelectedAchievementOnly)
            WatchSelectedAchievementOnly = true;
    }

    [RelayCommand(CanExecute = nameof(CanRefreshAchievements))]
    private async Task RefreshAchievements() => await LoadAchievements();

    [RelayCommand]
    private async Task SelectResearchTitle(ResearchTitleCard title)
    {
        if (IsSending || IsRangeTesting)
            return;

        SelectedResearchTitle = title;
        OnPropertyChanged(nameof(TitleInfo));
        OnPropertyChanged(nameof(HistoryPath));
        RefreshAchievementsCommand.NotifyCanExecuteChanged();
        CanUseProgressionDataTemplate = title.HasProgressionDataTemplate;
        EventTemplateInfo = BuildEventTemplateInfo(title);
        ProgressionData = "";
        PayloadPreview = "";
        ResultDetails = "";
        ProposedMapping = "";
        RangeAttempts.Clear();
        TestStatus = title.HasProgressionDataTemplate
            ? "Select an achievement and preview one ProgressionData candidate."
            : "Read-only analysis: this title does not have a usable REPLACEINDEX event template.";
        RangeStatus = title.HasProgressionDataTemplate
            ? "Range tester is idle."
            : "Range tester unavailable: no usable REPLACEINDEX event template.";
        LoadHistory();
        await LoadAchievements();
    }

    [RelayCommand]
    private void Preview()
    {
        if (!TryGetCandidate(out var candidate))
            return;

        try
        {
            PayloadPreview = BuildPayload(candidate, false);
            TestStatus = $"Preview ready for ProgressionData {candidate}. No event was sent.";
            ResultDetails = "";
        }
        catch (Exception ex)
        {
            TestStatus = $"Preview failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendResearchEvent))]
    private async Task SendOneTestEvent()
    {
        if (!TryGetCandidate(out var candidate) || !CheckEventsToken())
            return;

        var selectedId = SelectedAchievement!.Id;
        var selectedName = SelectedAchievement.Name;
        var before = new Dictionary<string, ResearchSnapshot>();
        var eventSent = false;

        IsSending = true;
        try
        {
            var beforeResponse = await FetchAchievements();
            if (beforeResponse == null)
            {
                TestStatus = "Cannot send: unable to load the before-achievement snapshot.";
                return;
            }

            ApplyAchievements(beforeResponse, selectedId);
            before = CaptureSnapshot(beforeResponse);
            var confirmation = await _contentDialogService.ShowSimpleDialogAsync(
                new SimpleContentDialogCreateOptions
                {
                    Title = $"Send one {SelectedResearchTitle!.Name} test event?",
                    Content =
                        $"Achievement: {selectedId} - {selectedName}\n" +
                        $"ProgressionData: {candidate}\n\n" +
                        "This sends exactly one event and will not edit Data.json.",
                    PrimaryButtonText = "Send one event",
                    CloseButtonText = "Cancel"
                });
            if (confirmation != ContentDialogResult.Primary)
            {
                TestStatus = "Test cancelled. No event was sent.";
                return;
            }

            PayloadPreview = BuildPayload(candidate, false);
            var requestBody = BuildPayload(candidate, true);
            var api = new XboxRestAPI(HomeViewModel.XAUTH);
            await api.UnlockEventBasedAchievement(
                AchievementsViewModel.EventsToken,
                new StringContent(requestBody, Encoding.UTF8, "application/x-json-stream"));
            eventSent = true;

            TestStatus = "Test event sent successfully. Checking Xbox achievement progress...";
            var after = new Dictionary<string, ResearchSnapshot>();
            var changes = "No achievement state, unlock time, or requirement changes detected.";
            var anyChanged = false;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(attempt == 0 ? 3000 : 4000);
                var afterResponse = await FetchAchievements();
                if (afterResponse == null)
                    continue;

                ApplyAchievements(afterResponse, selectedId);
                after = CaptureSnapshot(afterResponse);
                changes = CompareSnapshots(before, after, selectedId, out anyChanged);
                if (anyChanged)
                    break;
            }

            var entry = CreateHistoryEntry(candidate, selectedId, selectedName, before, after, changes, anyChanged);
            AchievementMappingResearchStore.Record(entry);
            ResultDetails = entry.Details;
            TestStatus = entry.Result switch
            {
                "Achievement unlocked" => "Test event sent successfully. Achievement unlocked.",
                "Progress changed" => "Test event sent successfully. Achievement progress changed.",
                "No effect" => "Test event sent successfully. No effect detected.",
                _ => "Test event sent; Xbox sync is delayed or an unknown state change was detected."
            };
            LoadHistory(true);
        }
        catch (Exception ex)
        {
            if (eventSent)
            {
                var entry = CreateHistoryEntry(
                    candidate,
                    selectedId,
                    selectedName,
                    before,
                    [],
                    $"Event sent; Xbox achievement data refresh failed: {ex.Message}",
                    true);
                AchievementMappingResearchStore.Record(entry);
                ResultDetails = entry.Details;
                TestStatus = "Test event sent; Xbox sync is delayed or the after snapshot could not be loaded.";
                LoadHistory(true);
            }
            else
            {
                TestStatus = $"Test event failed: {ex.Message}";
                ResultDetails = "No history record was saved because the one-event test did not complete.";
            }
        }
        finally
        {
            IsSending = false;
            UpdateEventTokenStatus();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartRangeTest))]
    private async Task StartRangeTest()
    {
        if (!TryGetRangeSettings(out var start, out var end, out var delaySeconds, out var maxAttempts)
            || !CheckEventsToken())
            return;

        var selectedId = SelectedAchievement!.Id;
        var selectedName = SelectedAchievement.Name;
        var confirmation = await _contentDialogService.ShowSimpleDialogAsync(
            new SimpleContentDialogCreateOptions
            {
                Title = $"Start controlled {SelectedResearchTitle!.Name} range test?",
                Content =
                    $"Achievement: {selectedId} - {selectedName}\n" +
                    $"Candidates: {start} through {end}\n" +
                    $"Maximum attempts: {maxAttempts}\n" +
                    $"Delay between attempts: {delaySeconds} second(s)\n\n" +
                    $"Watch scope: {(WatchAllAchievements ? "all achievements for the selected title" : "selected achievement only")}\n\n" +
                    "Candidates are sent one at a time. Every attempt is persisted. No Data.json changes are made.",
                PrimaryButtonText = "Start range test",
                CloseButtonText = "Cancel"
            });
        if (confirmation != ContentDialogResult.Primary)
        {
            RangeStatus = "Range test cancelled. No event was sent.";
            return;
        }

        _rangeCancellation = new CancellationTokenSource();
        var cancellationToken = _rangeCancellation.Token;
        var consecutiveFailures = 0;
        var attempts = 0;
        RangeAttempts.Clear();
        ProposedMapping = "";
        IsRangeTesting = true;
        RangeStatus = $"Range test started for {selectedId} - {selectedName}.";

        try
        {
            for (var candidate = start; candidate <= end && attempts < maxAttempts; candidate++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CheckEventsToken())
                {
                    RangeStatus = "Range test stopped: events token is missing or expired.";
                    break;
                }

                attempts++;
                RangeCurrentStatus = $"Attempt {attempts}/{maxAttempts}: sending ProgressionData {candidate}.";
                var result = await ExecuteRangeCandidate(
                    candidate, selectedId, selectedName, cancellationToken);

                RangeAttempts.Add(ResearchRangeAttemptRow.From(result.Entry, result.Status));
                ResultDetails = result.Entry.Details;
                TestStatus = result.Status;
                LoadHistory(true);

                if (result.Cancelled)
                {
                    RangeStatus = "Range test stopped by user after persisting the in-flight attempt.";
                    break;
                }

                consecutiveFailures = result.RequestFailed ? consecutiveFailures + 1 : 0;
                if (consecutiveFailures >= 2)
                {
                    RangeStatus = "Range test stopped after repeated request or snapshot failures.";
                    break;
                }

                var proposedMapping = BuildProposedMappingForResult(result, candidate);
                if (!string.IsNullOrWhiteSpace(proposedMapping))
                    ProposedMapping = proposedMapping;

                var hit = GetStopHit(result, selectedId);
                if (hit != null)
                {
                    RangeStatus = result.ChangeSummary.SelectedAchievementChanged
                        ? $"Candidate {candidate} affected achievement {hit}."
                        : $"Candidate {candidate} affected different achievement: {hit}.";
                    break;
                }

                if (candidate >= end || attempts >= maxAttempts)
                {
                    RangeStatus = $"Range test completed after {attempts} attempt(s) without a configured stop condition.";
                    break;
                }

                RangeCurrentStatus = $"Waiting {delaySeconds} second(s) before the next candidate.";
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            RangeStatus = "Range test stopped by user.";
        }
        finally
        {
            RangeCurrentStatus = $"Range test finished after {attempts} attempt(s).";
            IsRangeTesting = false;
            _rangeCancellation.Dispose();
            _rangeCancellation = null;
            UpdateEventTokenStatus();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopRangeTest))]
    private void StopRangeTest()
    {
        RangeStatus = "Stopping range test after the current in-flight request...";
        _rangeCancellation?.Cancel();
    }

    [RelayCommand]
    private void CopyResult()
    {
        if (string.IsNullOrWhiteSpace(ResultDetails) && string.IsNullOrWhiteSpace(PayloadPreview))
            return;

        Clipboard.SetText(
            $"{TestStatus}{Environment.NewLine}{Environment.NewLine}" +
            $"{ResultDetails}{Environment.NewLine}{Environment.NewLine}" +
            $"{RangeStatus}{Environment.NewLine}{ProposedMapping}{Environment.NewLine}{Environment.NewLine}" +
            $"Sanitized payload preview:{Environment.NewLine}{PayloadPreview}");
        ShowCopied("Test Result Copied", "The visible mapping research result was copied.");
    }

    [RelayCommand]
    private void RefreshHistory() => LoadHistory();

    [RelayCommand]
    private void CopySelectedEntry()
    {
        if (SelectedEntry == null)
            return;

        Clipboard.SetText(SelectedEntry.Details);
        ShowCopied("Research Record Copied", "The selected mapping research record was copied.");
    }

    [RelayCommand]
    private void CopyRequirementAnalysisSummary()
    {
        if (RequirementAnalysisRows.Count == 0)
            return;

        var rows = RequirementAnalysisRows.Select(row => string.Join('\t',
            row.AchievementId,
            row.AchievementName,
            row.State,
            row.MappingStatus,
            row.Classification,
            row.RequirementId,
            row.Current,
            row.Target,
            row.OperationType,
            row.ValueType,
            row.RuleParticipationType,
            row.Shape));
        Clipboard.SetText(
            $"{RequirementAnalysisSummary}{Environment.NewLine}{Environment.NewLine}" +
            "Achievement ID\tName\tState\tMapping status\tClassification\tRequirement ID\tCurrent\tTarget\tOperationType\tValueType\tRuleParticipationType\tShape" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, rows)}");
        ShowCopied("Structure Analysis Copied", "The achievement structure analysis was copied.");
    }

    private bool CanRefreshAchievements() => SelectedResearchTitle != null && !IsSending && !IsRangeTesting;

    private bool CanSendResearchEvent() => CanRefreshAchievements() && CanUseProgressionDataTemplate;

    private bool CanStartRangeTest() => CanSendResearchEvent();

    private bool CanStopRangeTest() => IsRangeTesting;

    private async Task LoadResearchTitles()
    {
        try
        {
            var eventsPath = GetEventsPath();
            var dataPath = Path.Combine(eventsPath, "Data.json");
            _eventData = JObject.Parse(File.ReadAllText(dataPath));
            var api = new XboxRestAPI(HomeViewModel.XAUTH);
            TitlesList games;
            try
            {
                games = await api.GetGamesListAsync(HomeViewModel.XUIDOnly) ?? new TitlesList();
            }
            catch
            {
                games = new TitlesList();
            }
            var gamesByTitleId = games.Titles
                .Where(title => !string.IsNullOrWhiteSpace(title.TitleId))
                .GroupBy(title => title.TitleId!)
                .ToDictionary(group => group.Key, group => group.First());

            var eventTitles = _eventData.Properties()
                .Where(property => property.Name.All(char.IsDigit))
                .ToDictionary(property => property.Name, property => property.Value as JObject);
            var templateTitleIds = Directory.EnumerateFiles(eventsPath, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(titleId => titleId?.All(char.IsDigit) == true)
                .Select(titleId => titleId!)
                .ToList();
            var titleIds = gamesByTitleId.Keys
                .Concat(eventTitles.Keys)
                .Concat(templateTitleIds)
                .Distinct()
                .ToList();

            var titles = titleIds.Select(titleId =>
            {
                gamesByTitleId.TryGetValue(titleId, out var game);
                eventTitles.TryGetValue(titleId, out var eventTitle);
                var mappings = eventTitle?["Achievements"] as JObject;
                var templatePath = Path.Combine(eventsPath, $"{titleId}.json");
                var hasEventTemplate = File.Exists(templatePath);
                var template = TryLoadTemplate(templatePath);
                var templateText = TryReadTemplate(templatePath);
                var history = AchievementMappingResearchStore.Load(titleId);
                var mappedCount = mappings?.Properties().Count() ?? 0;
                var fullySupported = eventTitle?["FullySupported"]?.Value<bool>() == true;
                var placeholders = GetTemplatePlaceholders(templateText);
                var replacementTypes = GetReplacementTypes(mappings);
                var usesReplaceIndex = placeholders.Contains("REPLACEINDEX");
                var hasUsableTemplate = template != null && usesReplaceIndex;
                var compatibility = hasUsableTemplate
                    ? ResearchCompatibility.ResearchSupported
                    : game != null || mappedCount > 0 || template != null
                        ? ResearchCompatibility.ReadOnlyAnalysis
                        : ResearchCompatibility.Unsupported;
                return new ResearchTitleCard
                {
                    TitleId = titleId,
                    Name = game?.Name ?? $"Title {titleId}",
                    Image = string.IsNullOrWhiteSpace(game?.DisplayImage)
                        ? "pack://application:,,,/Assets/cirno.png"
                        : game.DisplayImage!,
                    Scid = template?["data"]?["baseData"]?["serviceConfigId"]?.ToString()
                        ?? game?.ServiceConfigId
                        ?? "<unknown>",
                    MappedCount = mappedCount,
                    MissingCount = fullySupported ? "0" : "<load title>",
                    KnownHits = FormatCandidateValues(history
                        .Where(entry => entry.CandidateClassification != AchievementResearchClassifier.Unknown)
                        .Select(entry => entry.ProgressionData)
                        .Distinct()
                        .OrderBy(value => value)
                        .ToList()),
                    KnownMisses = FormatCandidateValues(history
                        .GroupBy(entry => entry.ProgressionData)
                        .Where(group => group.All(entry => entry.Result == "No effect"))
                        .Select(group => group.Key)
                        .OrderBy(value => value)
                        .ToList()),
                    TemplatePath = templatePath,
                    TemplateEventName = template?["name"]?.ToString() ?? "<template unavailable>",
                    HasEventTemplate = hasEventTemplate,
                    HasDataMappings = mappedCount > 0,
                    HasProgressionDataTemplate = hasUsableTemplate,
                    UsesReplaceIndex = usesReplaceIndex,
                    ReplacementTypes = FormatDiscoveryValues(replacementTypes),
                    OtherPlaceholders = FormatDiscoveryValues(placeholders
                        .Where(placeholder => placeholder != "REPLACEINDEX")
                        .ToList()),
                    Compatibility = compatibility
                };
            }).OrderByDescending(title => title.TitleId == AchievementMappingResearchStore.QuantumBreakTitleId)
                .ThenBy(title => title.CompatibilitySortOrder)
                .ThenBy(title => title.Name)
                .ToList();

            ResearchTitles = new ObservableCollection<ResearchTitleCard>(titles);
            FilterResearchTitles();
            TitleListStatus =
                $"Discovery found {titles.Count} title(s): " +
                $"{titles.Count(title => title.Compatibility == ResearchCompatibility.ResearchSupported)} research supported, " +
                $"{titles.Count(title => title.Compatibility == ResearchCompatibility.ReadOnlyAnalysis)} read-only analysis, " +
                $"{titles.Count(title => title.Compatibility == ResearchCompatibility.Unsupported)} unsupported.";

            var quantumBreak = titles.FirstOrDefault(title =>
                title.TitleId == AchievementMappingResearchStore.QuantumBreakTitleId);
            if (quantumBreak != null)
                await SelectResearchTitle(quantumBreak);
        }
        catch (Exception ex)
        {
            TitleListStatus = $"Research title load failed: {ex.Message}";
            ResearchTitles.Clear();
            FilteredResearchTitles.Clear();
        }
    }

    private void FilterResearchTitles()
    {
        var filtered = ResearchTitles.Where(title =>
            string.IsNullOrWhiteSpace(TitleSearchText)
            || title.Name.Contains(TitleSearchText, StringComparison.OrdinalIgnoreCase)
            || title.TitleId.Contains(TitleSearchText, StringComparison.OrdinalIgnoreCase)
            || title.Scid.Contains(TitleSearchText, StringComparison.OrdinalIgnoreCase)
            || title.Compatibility.Contains(TitleSearchText, StringComparison.OrdinalIgnoreCase)
            || title.ReplacementTypes.Contains(TitleSearchText, StringComparison.OrdinalIgnoreCase)
            || title.OtherPlaceholders.Contains(TitleSearchText, StringComparison.OrdinalIgnoreCase));
        FilteredResearchTitles = new ObservableCollection<ResearchTitleCard>(filtered);
    }

    private void RefreshResearchTitleCards()
    {
        ResearchTitles = new ObservableCollection<ResearchTitleCard>(ResearchTitles);
        FilterResearchTitles();
    }

    private static string BuildEventTemplateInfo(ResearchTitleCard title)
    {
        var template = TryLoadTemplate(title.TemplatePath);
        var templateText = TryReadTemplate(title.TemplatePath);
        if (template == null || templateText == null)
            return $"Compatibility: {title.Compatibility}{Environment.NewLine}" +
                $"Event template exists: {title.HasEventTemplate}{Environment.NewLine}" +
                $"Data.json mappings: {title.HasDataMappings} ({title.MappedCount}){Environment.NewLine}" +
                $"Template: {(title.HasEventTemplate ? "present but unreadable" : "unavailable")}{Environment.NewLine}" +
                $"Replacement types: {title.ReplacementTypes}{Environment.NewLine}" +
                $"Testing: read-only analysis only";

        var placeholders = GetTemplatePlaceholders(templateText);
        return $"Event: {title.TemplateEventName}{Environment.NewLine}" +
            $"Template: {title.TemplatePath}{Environment.NewLine}" +
            $"SCID: {title.Scid}{Environment.NewLine}" +
            $"Compatibility: {title.Compatibility}{Environment.NewLine}" +
            $"Event template exists: {title.HasEventTemplate}{Environment.NewLine}" +
            $"Data.json mappings: {title.HasDataMappings} ({title.MappedCount}){Environment.NewLine}" +
            $"Placeholders: {(placeholders.Count == 0 ? "<none>" : string.Join(", ", placeholders))}{Environment.NewLine}" +
            $"Replacement types: {title.ReplacementTypes}{Environment.NewLine}" +
            $"ProgressionData / REPLACEINDEX testing: {(title.HasProgressionDataTemplate ? "available" : "unavailable; read-only analysis only")}";
    }

    private static List<string> GetTemplatePlaceholders(string? templateText) =>
        templateText == null
            ? []
            : Regex.Matches(templateText, @"REPLACE[A-Z0-9_]+")
                .Select(match => match.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToList();

    private static List<string> GetReplacementTypes(JObject? mappings) =>
        mappings == null
            ? []
            : mappings.Descendants()
                .OfType<JProperty>()
                .Where(property => property.Name == "ReplacementType")
                .Select(property => property.Value.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct()
                .OrderBy(value => value)
                .ToList();

    private static string FormatDiscoveryValues(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? "<none>" : string.Join(", ", values);

    private static JObject? TryLoadTemplate(string path)
    {
        try
        {
            var template = TryReadTemplate(path);
            return template == null
                ? null
                : JObject.Parse(Regex.Replace(template, @"REPLACE[A-Z0-9_]+", "0"));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadTemplate(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetEventsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "Events");

    private async Task LoadAchievements()
    {
        IsSending = true;
        try
        {
            UpdateEventTokenStatus();
            var response = await FetchAchievements();
            if (response == null)
            {
                AchievementStatus = "Achievements could not be loaded. Ensure Xbox authentication is connected.";
                Achievements.Clear();
                ClearRequirementAnalysis();
                return;
            }

            ApplyAchievements(response, SelectedAchievement?.Id);
            AchievementStatus = $"Loaded {Achievements.Count} achievement(s) for {SelectedResearchTitle!.Name} from Xbox.";
            SelectedResearchTitle.MissingCount = Math.Max(0, Achievements.Count - SelectedResearchTitle.MappedCount).ToString();
            RefreshResearchTitleCards();
        }
        catch (Exception ex)
        {
            AchievementStatus = $"Achievement load failed: {ex.Message}";
            Achievements.Clear();
            ClearRequirementAnalysis();
        }
        finally
        {
            IsSending = false;
        }
    }

    private async Task<AchievementsResponse?> FetchAchievements()
    {
        if (SelectedResearchTitle == null)
            return null;

        var api = new XboxRestAPI(HomeViewModel.XAUTH);
        return await api.GetAchievementsForTitleAsync(HomeViewModel.XUIDOnly, SelectedResearchTitle.TitleId);
    }

    private void ApplyAchievements(AchievementsResponse response, string? selectedId)
    {
        var mappings = LoadSelectedTitleMappings();
        Achievements = new ObservableCollection<ResearchAchievement>(
            response.achievements.Select(achievement =>
            {
                var requirements = achievement.progression?.requirements ?? [];
                var mapping = mappings?[achievement.id] as JObject;
                return new ResearchAchievement
                {
                    Id = achievement.id,
                    Name = achievement.name,
                    State = achievement.progressState,
                    MappingStatus = FormatMappingStatus(mapping),
                    Classification = AchievementResearchClassifier.ClassifyAchievement(
                        SelectedResearchTitle!.TitleId, achievement.id, _allHistoryEntries),
                    Current = FormatRequirementValues(requirements, requirement => requirement.current),
                    Target = FormatRequirementValues(requirements, requirement => requirement.target)
                };
            }).OrderBy(achievement => int.TryParse(achievement.Id, out var id) ? id : int.MaxValue));

        SelectedAchievement = Achievements.FirstOrDefault(achievement => achievement.Id == selectedId)
            ?? Achievements.FirstOrDefault();
        BuildRequirementAnalysis(response, mappings);
    }

    private void BuildRequirementAnalysis(AchievementsResponse response, JObject? mappings)
    {
        var priorityUnsupportedIds = new HashSet<string> { "19", "28", "29", "47", "48" };
        var rows = response.achievements.SelectMany(achievement =>
        {
            var mappingStatus = FormatMappingStatus(mappings?[achievement.id] as JObject);
            var classification = AchievementResearchClassifier.ClassifyAchievement(
                SelectedResearchTitle!.TitleId, achievement.id, _allHistoryEntries);
            var isPriorityUnsupported = SelectedResearchTitle.TitleId == AchievementMappingResearchStore.QuantumBreakTitleId
                && priorityUnsupportedIds.Contains(achievement.id)
                && mappingStatus.StartsWith("Unsupported", StringComparison.Ordinal)
                && !string.Equals(achievement.progressState, "Achieved", StringComparison.OrdinalIgnoreCase);
            var requirements = achievement.progression?.requirements ?? [];
            return requirements.Count == 0
                ? [CreateRequirementAnalysisRow(achievement, null, mappingStatus, classification, isPriorityUnsupported)]
                : requirements.Select(requirement =>
                    CreateRequirementAnalysisRow(achievement, requirement, mappingStatus, classification, isPriorityUnsupported));
        }).OrderBy(row => int.TryParse(row.AchievementId, out var id) ? id : int.MaxValue)
            .ThenBy(row => row.RequirementId);

        RequirementAnalysisRows = new ObservableCollection<ResearchRequirementAnalysisRow>(rows);
        UpdateRequirementAnalysisSummary();
    }

    private static ResearchRequirementAnalysisRow CreateRequirementAnalysisRow(
        OneCoreAchievementResponse achievement,
        AchievementRequirements? requirement,
        string mappingStatus,
        string classification,
        bool isPriorityUnsupported) =>
        new()
        {
            AchievementId = achievement.id,
            AchievementName = achievement.name,
            State = achievement.progressState,
            MappingStatus = mappingStatus,
            Classification = classification,
            RequirementId = requirement?.id ?? "<none>",
            Current = requirement?.current ?? "<empty>",
            Target = requirement?.target ?? "<none>",
            OperationType = requirement?.operationType ?? "<none>",
            ValueType = requirement?.valueType ?? "<none>",
            RuleParticipationType = requirement?.ruleParticipationType ?? "<none>",
            Shape = ClassifyRequirementShape(requirement),
            IsPriorityUnsupported = isPriorityUnsupported
        };

    private static string ClassifyRequirementShape(AchievementRequirements? requirement)
    {
        if (requirement == null)
            return "other";

        var current = requirement.current;
        var target = requirement.target;
        if (string.IsNullOrWhiteSpace(current)
            && decimal.TryParse(target, NumberStyles.Number, CultureInfo.InvariantCulture, out var emptyTarget)
            && emptyTarget == 0)
            return "empty current + target 0";

        if (requirement.valueType?.Contains("Integer", StringComparison.OrdinalIgnoreCase) == true
            || long.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                && long.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return "integer progress";

        if (requirement.valueType?.Contains("Double", StringComparison.OrdinalIgnoreCase) == true
            || requirement.valueType?.Contains("Float", StringComparison.OrdinalIgnoreCase) == true
            || decimal.TryParse(current, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                && decimal.TryParse(target, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            return "double/fraction progress";

        return "other";
    }

    private void ClearRequirementAnalysis()
    {
        RequirementAnalysisRows.Clear();
        RequirementAnalysisSummary = "Load a title's achievements to analyse requirement structures.";
    }

    private void UpdateRequirementAnalysisSummary()
    {
        if (RequirementAnalysisRows.Count == 0)
            return;

        var achievements = RequirementAnalysisRows
            .GroupBy(row => row.AchievementId)
            .Select(group => group.First())
            .ToList();
        var priorityUnsupported = RequirementAnalysisRows
            .Where(row => row.IsPriorityUnsupported)
            .GroupBy(row => row.AchievementId)
            .Select(group =>
                $"{group.Key} - {group.First().AchievementName}: {group.First().State}; " +
                $"{group.First().MappingStatus}; {group.First().Classification}; " +
                $"shapes={string.Join(", ", group.Select(row => row.Shape).Distinct())}")
            .ToList();
        var knownHits = _allHistoryEntries
            .Where(entry => entry.ChangedAchievements.Count > 0
                || entry.Result is "Progress changed" or "Achievement unlocked"
                || entry.CandidateClassification != AchievementResearchClassifier.Unknown)
            .Select(entry => entry.ProgressionData)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        var misses = _allHistoryEntries
            .GroupBy(entry => entry.ProgressionData)
            .Where(group => group.All(entry => entry.Result == "No effect"))
            .Select(group => group.Key)
            .OrderBy(value => value)
            .ToList();
        var differentAchievementHits = _allHistoryEntries
            .SelectMany(entry => entry.ChangedAchievements
                .Where(change => !change.IsSelectedAchievement)
                .Select(change => $"{entry.ProgressionData} -> {change.AchievementId} - {change.AchievementName}"))
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        RequirementAnalysisSummary =
            $"Achievements: {achievements.Count}; mapped={achievements.Count(row => row.MappingStatus.StartsWith("Mapped", StringComparison.Ordinal))}; " +
            $"missing mapping={achievements.Count(row => row.MappingStatus.StartsWith("Unsupported", StringComparison.Ordinal))}{Environment.NewLine}" +
            $"Current/target shapes: {FormatAnalysisGroups(RequirementAnalysisRows.GroupBy(row => row.Shape))}{Environment.NewLine}" +
            $"Research classifications: {FormatAnalysisGroups(achievements.GroupBy(row => row.Classification))}{Environment.NewLine}" +
            $"ValueType: {FormatAnalysisGroups(RequirementAnalysisRows.GroupBy(row => row.ValueType))}{Environment.NewLine}" +
            $"OperationType: {FormatAnalysisGroups(RequirementAnalysisRows.GroupBy(row => row.OperationType))}{Environment.NewLine}" +
            $"Highlighted locked/unsupported achievements:{Environment.NewLine}{FormatAnalysisLines(priorityUnsupported)}{Environment.NewLine}" +
            $"Known hits: {FormatCandidateValues(knownHits)}{Environment.NewLine}" +
            $"Known misses: {FormatCandidateValues(misses)}{Environment.NewLine}" +
            $"Candidates that affected different achievements:{Environment.NewLine}{FormatAnalysisLines(differentAchievementHits)}";
    }

    private static string FormatAnalysisGroups(IEnumerable<IGrouping<string, ResearchRequirementAnalysisRow>> groups) =>
        string.Join(", ", groups.OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));

    private static string FormatAnalysisLines(IReadOnlyCollection<string> lines) =>
        lines.Count == 0 ? "<none>" : string.Join(Environment.NewLine, lines);

    private JObject? LoadSelectedTitleMappings()
    {
        return SelectedResearchTitle == null
            ? null
            : _eventData?[SelectedResearchTitle.TitleId]?["Achievements"] as JObject;
    }

    private static string FormatMappingStatus(JObject? mapping)
    {
        if (mapping == null)
            return "Unsupported: missing event mapping";

        var progressionData = mapping.Properties()
            .Select(property => property.Value)
            .FirstOrDefault(value => value["Target"]?.ToString() == "REPLACEINDEX")?["Replacement"]?.ToString();
        return string.IsNullOrWhiteSpace(progressionData)
            ? "Mapped"
            : $"Mapped: ProgressionData {progressionData}";
    }

    private static string FormatRequirementValues(
        IReadOnlyList<AchievementRequirements> requirements,
        Func<AchievementRequirements, string?> selector) =>
        requirements.Count == 0
            ? "<none>"
            : string.Join(", ", requirements.Select(requirement => selector(requirement) ?? "<none>"));

    private bool TryGetCandidate(out int candidate)
    {
        candidate = 0;
        if (SelectedResearchTitle == null)
        {
            TestStatus = "Select an event-based title first.";
            return false;
        }

        if (!CanUseProgressionDataTemplate)
        {
            TestStatus = "This title is read-only because its event template does not support REPLACEINDEX.";
            return false;
        }

        if (SelectedAchievement == null)
        {
            TestStatus = "Select an achievement first.";
            return false;
        }

        if (!int.TryParse(ProgressionData, NumberStyles.Integer, CultureInfo.InvariantCulture, out candidate))
        {
            TestStatus = "Enter one valid ProgressionData integer.";
            return false;
        }

        return true;
    }

    private bool CheckEventsToken()
    {
        UpdateEventTokenStatus();
        if (string.IsNullOrWhiteSpace(AchievementsViewModel.EventsToken))
        {
            TestStatus = "Cannot send: the events token is missing.";
            RangeStatus = "Cannot start or continue: the events token is missing.";
            return false;
        }

        if (HomeViewModel.IsEventsTokenExpired())
        {
            TestStatus = "Cannot send: the events token is expired.";
            RangeStatus = "Cannot start or continue: the events token is expired.";
            return false;
        }

        return true;
    }

    private void UpdateEventTokenStatus()
    {
        EventTokenStatus = string.IsNullOrWhiteSpace(AchievementsViewModel.EventsToken)
            ? "Events token: missing. Capture or refresh it before sending."
            : HomeViewModel.IsEventsTokenExpired()
                ? "Events token: expired. Refresh it before sending."
                : "Events token: available for one confirmed test event.";
    }

    private string BuildPayload(int candidate, bool forSend)
    {
        if (SelectedResearchTitle == null || !CanUseProgressionDataTemplate)
            throw new InvalidOperationException("The selected title does not have a usable REPLACEINDEX event template.");

        var requestBody = File.ReadAllText(SelectedResearchTitle.TemplatePath)
            .Replace("REPLACEINDEX", candidate.ToString(CultureInfo.InvariantCulture))
            .Replace("REPLACESEQ", forSend ? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() : "0")
            .Replace("REPLACEXUID", forSend ? HomeViewModel.XUIDOnly : "REDACTED_XUID")
            .Replace("REPLACETIME", forSend
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
                : "CURRENT_UTC_TIME_AT_SEND");
        return JObject.Parse(requestBody).ToString(forSend ? Formatting.None : Formatting.Indented);
    }

    private async Task<RangeCandidateResult> ExecuteRangeCandidate(
        int candidate,
        string selectedId,
        string selectedName,
        CancellationToken cancellationToken)
    {
        var before = new Dictionary<string, ResearchSnapshot>();
        var after = new Dictionary<string, ResearchSnapshot>();
        var eventSent = false;

        try
        {
            var beforeResponse = await FetchAchievements();
            if (beforeResponse == null)
                return PersistRangeFailure(candidate, selectedId, selectedName, before, "Before snapshot request returned no data.");

            ApplyAchievements(beforeResponse, selectedId);
            before = CaptureSnapshot(beforeResponse);
            cancellationToken.ThrowIfCancellationRequested();

            PayloadPreview = BuildPayload(candidate, false);
            var requestBody = BuildPayload(candidate, true);
            var api = new XboxRestAPI(HomeViewModel.XAUTH);
            await api.UnlockEventBasedAchievement(
                AchievementsViewModel.EventsToken,
                new StringContent(requestBody, Encoding.UTF8, "application/x-json-stream"));
            eventSent = true;

            var changes = "No achievement state, unlock time, or requirement changes detected.";
            var anyChanged = false;
            for (var refreshAttempt = 0; refreshAttempt < 3; refreshAttempt++)
            {
                await Task.Delay(refreshAttempt == 0 ? 3000 : 4000, cancellationToken);
                var afterResponse = await FetchAchievements();
                if (afterResponse == null)
                    continue;

                ApplyAchievements(afterResponse, selectedId);
                after = CaptureSnapshot(afterResponse);
                changes = CompareSnapshots(before, after, selectedId, out anyChanged);
                if (anyChanged)
                    break;
            }

            var entry = CreateHistoryEntry(candidate, selectedId, selectedName, before, after, changes, anyChanged);
            AchievementMappingResearchStore.Record(entry);
            var changeSummary = AnalyzeSnapshotChanges(before, after, selectedId);
            return new RangeCandidateResult
            {
                Entry = entry,
                Status = FormatAttemptStatus(candidate, entry),
                RequestFailed = after.Count == 0,
                ChangeSummary = changeSummary
            };
        }
        catch (OperationCanceledException) when (eventSent)
        {
            var result = PersistRangeFailure(
                candidate,
                selectedId,
                selectedName,
                before,
                "Event sent; after snapshot was cancelled before Xbox sync completed.");
            result.Cancelled = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = PersistRangeFailure(
                candidate,
                selectedId,
                selectedName,
                before,
                "Candidate cancelled before the event was sent.");
            result.Cancelled = true;
            return result;
        }
        catch (Exception ex)
        {
            return PersistRangeFailure(
                candidate,
                selectedId,
                selectedName,
                before,
                eventSent
                    ? $"Event sent; Xbox achievement data refresh failed: {ex.Message}"
                    : $"Candidate was not sent because the request failed: {ex.Message}");
        }
    }

    private RangeCandidateResult PersistRangeFailure(
        int candidate,
        string selectedId,
        string selectedName,
        Dictionary<string, ResearchSnapshot> before,
        string status)
    {
        var entry = CreateHistoryEntry(candidate, selectedId, selectedName, before, [], status, true);
        AchievementMappingResearchStore.Record(entry);
        return new RangeCandidateResult
        {
            Entry = entry,
            Status = status,
            RequestFailed = true,
            ChangeSummary = new SnapshotChangeSummary()
        };
    }

    private bool TryGetRangeSettings(out int start, out int end, out int delaySeconds, out int maxAttempts)
    {
        start = end = delaySeconds = maxAttempts = 0;
        if (SelectedAchievement == null)
        {
            RangeStatus = "Select an achievement first.";
            return false;
        }

        if (!int.TryParse(RangeStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
            || !int.TryParse(RangeEnd, NumberStyles.Integer, CultureInfo.InvariantCulture, out end)
            || start > end)
        {
            RangeStatus = "Enter a valid ProgressionData range where start is less than or equal to end.";
            return false;
        }

        if (!int.TryParse(RangeDelaySeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out delaySeconds)
            || delaySeconds < 1)
        {
            RangeStatus = "Delay between attempts must be at least 1 second.";
            return false;
        }

        if (!int.TryParse(RangeMaxAttempts, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxAttempts)
            || maxAttempts < 1
            || maxAttempts > 25)
        {
            RangeStatus = "Maximum attempts must be between 1 and 25.";
            return false;
        }

        return true;
    }

    private string? GetStopHit(RangeCandidateResult result, string selectedId)
    {
        var summary = result.ChangeSummary;
        var watchedChanges = WatchAllAchievements
            ? summary.Changes
            : summary.Changes.Where(change => change.IsSelectedAchievement).ToList();
        var shouldStop = StopOnSelectedAchievementChange && summary.SelectedAchievementChanged
            || StopOnAnyAchievementChange && watchedChanges.Count > 0
            || StopOnAchievementUnlocked && watchedChanges.Any(change => change.AchievementUnlocked)
            || StopOnStateChanged && watchedChanges.Any(change => change.StateChanged)
            || StopOnRequirementChanged && watchedChanges.Any(change => change.RequirementChanged);
        if (!shouldStop)
            return null;

        return watchedChanges.Count == 0
            ? selectedId
            : string.Join(", ", watchedChanges.Select(change => $"{change.AchievementId} - {change.AchievementName}"));
    }

    private SnapshotChangeSummary AnalyzeSnapshotChanges(
        Dictionary<string, ResearchSnapshot> before,
        Dictionary<string, ResearchSnapshot> after,
        string selectedId)
    {
        var result = new SnapshotChangeSummary();
        if (before.Count == 0 || after.Count == 0)
            return result;

        foreach (var achievementId in before.Keys.Union(after.Keys).OrderBy(id => id))
        {
            before.TryGetValue(achievementId, out var beforeAchievement);
            after.TryGetValue(achievementId, out var afterAchievement);
            var unlocked = beforeAchievement != null && afterAchievement != null
                && !string.Equals(beforeAchievement.State, StringConstants.Achieved, StringComparison.OrdinalIgnoreCase)
                && string.Equals(afterAchievement.State, StringConstants.Achieved, StringComparison.OrdinalIgnoreCase);
            var stateChanged = beforeAchievement?.State != afterAchievement?.State;
            var unlockTimeChanged = beforeAchievement?.TimeUnlocked != afterAchievement?.TimeUnlocked;
            var requirementDiff = BuildRequirementDiff(
                beforeAchievement?.Requirements ?? [],
                afterAchievement?.Requirements ?? []);
            var requirementsChanged = requirementDiff.Count > 0;
            if (!stateChanged && !unlockTimeChanged && !requirementsChanged)
                continue;

            var change = new ResearchChangedAchievement
            {
                AchievementId = achievementId,
                AchievementName = afterAchievement?.Name ?? beforeAchievement?.Name ?? "",
                IsSelectedAchievement = achievementId == selectedId,
                AchievementUnlocked = unlocked,
                StateChanged = stateChanged,
                UnlockTimeChanged = unlockTimeChanged,
                RequirementChanged = requirementsChanged,
                BeforeState = beforeAchievement?.State,
                AfterState = afterAchievement?.State,
                BeforeUnlockTime = beforeAchievement?.TimeUnlocked,
                AfterUnlockTime = afterAchievement?.TimeUnlocked,
                BeforeRequirements = beforeAchievement?.Requirements.ToDictionary(item => item.Key, item => item.Value) ?? [],
                AfterRequirements = afterAchievement?.Requirements.ToDictionary(item => item.Key, item => item.Value) ?? [],
                RequirementDiff = string.Join("; ", requirementDiff)
            };
            change.Classification = AchievementResearchClassifier.ClassifyAchievement(
                SelectedResearchTitle!.TitleId, achievementId, change);
            result.Changes.Add(change);
        }

        return result;
    }

    private static string FormatAttemptStatus(int candidate, AchievementMappingResearchEntry entry) =>
        entry.Result switch
        {
            "Achievement unlocked" => $"Candidate {candidate}: achievement unlocked.",
            "Progress changed" => $"Candidate {candidate}: achievement progress changed.",
            "No effect" => $"Candidate {candidate}: no effect detected.",
            _ => $"Candidate {candidate}: delayed Xbox sync or unknown state change."
        };

    private static string BuildProposedMapping(string achievementId, int candidate) =>
        new JObject
        {
            [achievementId] = new JObject
            {
                ["Replacement"] = new JObject
                {
                    ["ReplacementType"] = "Replace",
                    ["Target"] = "REPLACEINDEX",
                    ["Replacement"] = candidate
                }
            }
        }.ToString(Formatting.Indented);

    private static string BuildProposedMappingForResult(RangeCandidateResult result, int candidate)
    {
        if (result.ChangeSummary.Changes.Count == 0)
            return "";
        if (result.ChangeSummary.Changes.Count > 1)
            return "Ambiguous: multiple achievements changed. No single proposed mapping was generated." +
                Environment.NewLine +
                string.Join(Environment.NewLine, result.ChangeSummary.Changes.Select(change =>
                    $"{change.AchievementId} - {change.AchievementName}"));

        return BuildProposedMapping(result.ChangeSummary.Changes[0].AchievementId, candidate);
    }

    private static Dictionary<string, ResearchSnapshot> CaptureSnapshot(AchievementsResponse response) =>
        response.achievements.ToDictionary(
            achievement => achievement.id,
            achievement => new ResearchSnapshot
            {
                Name = achievement.name,
                State = achievement.progressState,
                TimeUnlocked = achievement.progression?.timeUnlocked,
                Requirements = (achievement.progression?.requirements ?? [])
                    .Select((requirement, index) => new
                    {
                        Key = string.IsNullOrWhiteSpace(requirement.id) ? $"#{index}" : requirement.id,
                        requirement.current
                    })
                    .ToDictionary(requirement => requirement.Key!, requirement => requirement.current)
            });

    private string CompareSnapshots(
        Dictionary<string, ResearchSnapshot> before,
        Dictionary<string, ResearchSnapshot> after,
        string selectedId,
        out bool anyChanged)
    {
        var summary = AnalyzeSnapshotChanges(before, after, selectedId);
        anyChanged = summary.Changes.Count > 0;
        return anyChanged
            ? string.Join(Environment.NewLine + Environment.NewLine, summary.Changes.Select(change => change.Details))
            : "No achievement state, unlock time, or requirement changes detected.";
    }

    private static List<string> BuildRequirementDiff(
        Dictionary<string, string?> before,
        Dictionary<string, string?> after)
    {
        var lines = new List<string>();
        foreach (var requirementId in before.Keys.Union(after.Keys).OrderBy(id => id))
        {
            var beforeExists = before.TryGetValue(requirementId, out var beforeValue);
            var afterExists = after.TryGetValue(requirementId, out var afterValue);
            if (beforeExists && afterExists && beforeValue == afterValue)
                continue;

            var delta = beforeExists && afterExists
                && decimal.TryParse(beforeValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var beforeNumber)
                && decimal.TryParse(afterValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var afterNumber)
                    ? $" ({afterNumber - beforeNumber:+0.################;-0.################;0})"
                    : "";
            var change = !beforeExists ? "added" : !afterExists ? "removed" : "changed";
            lines.Add($"{requirementId} {change}: {beforeValue ?? "<none>"} -> {afterValue ?? "<none>"}{delta}");
        }

        return lines;
    }

    private AchievementMappingResearchEntry CreateHistoryEntry(
        int candidate,
        string selectedId,
        string selectedName,
        Dictionary<string, ResearchSnapshot> before,
        Dictionary<string, ResearchSnapshot> after,
        string observedChanges,
        bool anyChanged)
    {
        before.TryGetValue(selectedId, out var beforeSelected);
        after.TryGetValue(selectedId, out var afterSelected);
        var summary = AnalyzeSnapshotChanges(before, after, selectedId);
        var changedAchievements = summary.Changes;

        return new AchievementMappingResearchEntry
        {
            TitleId = SelectedResearchTitle!.TitleId,
            ProgressionData = candidate,
            AchievementId = selectedId,
            AchievementName = beforeSelected?.Name ?? afterSelected?.Name ?? selectedName,
            Result = summary.AchievementUnlocked
                ? "Achievement unlocked"
                : summary.RequirementChanged
                    ? "Progress changed"
                    : changedAchievements.Count > 0 || anyChanged || afterSelected == null
                        ? "Unknown state change"
                        : "No effect",
            BeforeState = beforeSelected?.State,
            AfterState = afterSelected?.State,
            BeforeUnlockTime = beforeSelected?.TimeUnlocked,
            AfterUnlockTime = afterSelected?.TimeUnlocked,
            BeforeRequirements = beforeSelected?.Requirements.ToDictionary(item => item.Key, item => item.Value) ?? [],
            AfterRequirements = afterSelected?.Requirements.ToDictionary(item => item.Key, item => item.Value) ?? [],
            ChangedAchievements = changedAchievements,
            ObservedChanges = observedChanges
        };
    }

    private void LoadHistory(bool selectNewest = false)
    {
        var selectedRecordedAt = selectNewest ? null : SelectedEntry?.RecordedAtUtc;
        if (SelectedResearchTitle == null)
        {
            Entries.Clear();
            SelectedEntry = null;
            HistoryStatus = "Select a title to load its research history.";
            CandidateHistorySummary = "No title selected.";
            _allHistoryEntries = [];
            return;
        }

        var allEntries = AchievementMappingResearchStore.Load(SelectedResearchTitle.TitleId)
            .OrderByDescending(entry => entry.RecordedAtUtc)
            .ToList();
        _allHistoryEntries = allEntries;
        RefreshAchievementClassifications();
        var filtered = allEntries.Where(entry => entry.Matches(SearchText)).ToList();

        Entries = new ObservableCollection<AchievementMappingResearchEntry>(filtered);
        SelectedEntry = selectedRecordedAt.HasValue
            ? Entries.FirstOrDefault(entry => entry.RecordedAtUtc == selectedRecordedAt.Value)
            : Entries.FirstOrDefault();
        HistoryStatus = $"Showing {filtered.Count} of {allEntries.Count} persisted research record(s).";
        CandidateHistorySummary = BuildCandidateHistorySummary(allEntries);
        SelectedResearchTitle.KnownHits = FormatCandidateValues(allEntries
            .Where(entry => entry.CandidateClassification != AchievementResearchClassifier.Unknown)
            .Select(entry => entry.ProgressionData)
            .Distinct()
            .OrderBy(value => value)
            .ToList());
        SelectedResearchTitle.KnownMisses = FormatCandidateValues(allEntries
            .GroupBy(entry => entry.ProgressionData)
            .Where(group => group.All(entry => entry.Result == "No effect"))
            .Select(group => group.Key)
            .OrderBy(value => value)
            .ToList());
        RefreshResearchTitleCards();
        UpdateRequirementAnalysisSummary();
    }

    private void RefreshAchievementClassifications()
    {
        var selectedId = SelectedAchievement?.Id;
        foreach (var achievement in Achievements)
            achievement.Classification = AchievementResearchClassifier.ClassifyAchievement(
                SelectedResearchTitle!.TitleId, achievement.Id, _allHistoryEntries);
        if (Achievements.Count > 0)
        {
            Achievements = new ObservableCollection<ResearchAchievement>(Achievements);
            SelectedAchievement = Achievements.FirstOrDefault(achievement => achievement.Id == selectedId)
                ?? Achievements.FirstOrDefault();
        }

        foreach (var row in RequirementAnalysisRows)
            row.Classification = AchievementResearchClassifier.ClassifyAchievement(
                SelectedResearchTitle!.TitleId, row.AchievementId, _allHistoryEntries);
        if (RequirementAnalysisRows.Count > 0)
            RequirementAnalysisRows = new ObservableCollection<ResearchRequirementAnalysisRow>(
                RequirementAnalysisRows);
    }

    private static string BuildCandidateHistorySummary(IReadOnlyCollection<AchievementMappingResearchEntry> entries)
    {
        var tested = entries.Select(entry => entry.ProgressionData).Distinct().OrderBy(value => value).ToList();
        if (tested.Count == 0)
            return "Known hits: <none>\nKnown misses: <none>\nUntested ranges: <none>";

        var knownHits = entries
            .Where(entry => entry.CandidateClassification != AchievementResearchClassifier.Unknown)
            .Select(entry => entry.ProgressionData)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        var detectedHits = entries
            .Where(entry => entry.ChangedAchievements.Count > 0
                || entry.Result is "Progress changed" or "Achievement unlocked")
            .Select(entry => entry.ProgressionData)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        var misses = entries
            .GroupBy(entry => entry.ProgressionData)
            .Where(group => group.All(entry => entry.Result == "No effect"))
            .Select(group => group.Key)
            .OrderBy(value => value)
            .ToList();
        var untested = Enumerable.Range(tested[0], tested[^1] - tested[0] + 1)
            .Except(tested)
            .ToList();
        var classifications = entries
            .GroupBy(entry => entry.CandidateClassification)
            .OrderBy(group => group.Key)
            .Select(group =>
                $"{group.Key}: {FormatCandidateValues(group.Select(entry => entry.ProgressionData).Distinct().OrderBy(value => value).ToList())}");

        return $"Known hits: {FormatCandidateValues(knownHits)}{Environment.NewLine}" +
            $"Detected hits: {FormatCandidateValues(detectedHits)}{Environment.NewLine}" +
            $"Known misses: {FormatCandidateValues(misses)}{Environment.NewLine}" +
            $"Untested ranges within {tested[0]}-{tested[^1]}: {FormatCandidateRanges(untested)}{Environment.NewLine}" +
            $"Candidate classifications:{Environment.NewLine}{string.Join(Environment.NewLine, classifications)}";
    }

    private static string FormatCandidateValues(IReadOnlyCollection<int> values) =>
        values.Count == 0 ? "<none>" : string.Join(", ", values);

    private static string FormatCandidateRanges(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            return "<none>";

        var ranges = new List<string>();
        var start = values[0];
        var previous = start;
        foreach (var value in values.Skip(1))
        {
            if (value == previous + 1)
            {
                previous = value;
                continue;
            }

            ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = value;
        }
        ranges.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return string.Join(", ", ranges);
    }

    private void ShowCopied(string title, string message) =>
        _snackbarService.Show(
            title,
            message,
            ControlAppearance.Success,
            new SymbolIcon(SymbolRegular.ClipboardCheckmark24),
            _snackbarDuration);

    private sealed class ResearchSnapshot
    {
        public string Name { get; set; } = "";
        public string? State { get; set; }
        public string? TimeUnlocked { get; set; }
        public Dictionary<string, string?> Requirements { get; set; } = new();
    }

    private sealed class RangeCandidateResult
    {
        public required AchievementMappingResearchEntry Entry { get; init; }
        public string Status { get; init; } = "";
        public bool RequestFailed { get; init; }
        public bool Cancelled { get; set; }
        public SnapshotChangeSummary ChangeSummary { get; init; } = new();
        public bool SelectedAchievementChanged => ChangeSummary.SelectedAchievementChanged;
    }

    private sealed class SnapshotChangeSummary
    {
        public List<ResearchChangedAchievement> Changes { get; } = new();
        public bool AchievementUnlocked => Changes.Any(change => change.AchievementUnlocked);
        public bool StateChanged => Changes.Any(change => change.StateChanged);
        public bool RequirementChanged => Changes.Any(change => change.RequirementChanged);
        public bool SelectedAchievementChanged => Changes.Any(change => change.IsSelectedAchievement);
    }
}

public sealed class ResearchAchievement
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string MappingStatus { get; set; } = "";
    public string Classification { get; set; } = AchievementResearchClassifier.Unknown;
    public string Current { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class ResearchTitleCard
{
    public string TitleId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
    public string Scid { get; set; } = "<unknown>";
    public int MappedCount { get; set; }
    public string MissingCount { get; set; } = "<load title>";
    public string KnownHits { get; set; } = "<none>";
    public string KnownMisses { get; set; } = "<none>";
    public string TemplatePath { get; set; } = "";
    public string TemplateEventName { get; set; } = "<template unavailable>";
    public bool HasEventTemplate { get; set; }
    public bool HasDataMappings { get; set; }
    public bool HasProgressionDataTemplate { get; set; }
    public bool UsesReplaceIndex { get; set; }
    public string ReplacementTypes { get; set; } = "<none>";
    public string OtherPlaceholders { get; set; } = "<none>";
    public string Compatibility { get; set; } = ResearchCompatibility.Unsupported;
    public int CompatibilitySortOrder => Compatibility switch
    {
        ResearchCompatibility.ResearchSupported => 0,
        ResearchCompatibility.ReadOnlyAnalysis => 1,
        _ => 2
    };
    public string KnownHitsDisplay => $"Hits: {KnownHits}";
    public string KnownMissesDisplay => $"Misses: {KnownMisses}";
    public string DiscoverySummary =>
        $"Template: {(HasEventTemplate ? "yes" : "no")} | Mappings: {(HasDataMappings ? "yes" : "no")} | REPLACEINDEX: {(UsesReplaceIndex ? "yes" : "no")}";
    public string ReplacementSummary => $"Types: {ReplacementTypes}";
}

public static class ResearchCompatibility
{
    public const string ResearchSupported = "Research Supported";
    public const string ReadOnlyAnalysis = "Read-Only Analysis";
    public const string Unsupported = "Unsupported";
}

public sealed class ResearchRequirementAnalysisRow
{
    public string AchievementId { get; set; } = "";
    public string AchievementName { get; set; } = "";
    public string State { get; set; } = "";
    public string MappingStatus { get; set; } = "";
    public string Classification { get; set; } = AchievementResearchClassifier.Unknown;
    public string RequirementId { get; set; } = "";
    public string Current { get; set; } = "";
    public string Target { get; set; } = "";
    public string OperationType { get; set; } = "";
    public string ValueType { get; set; } = "";
    public string RuleParticipationType { get; set; } = "";
    public string Shape { get; set; } = "";
    public bool IsPriorityUnsupported { get; set; }
}

public sealed class ResearchRangeAttemptRow
{
    public int ProgressionData { get; set; }
    public string Achievement { get; set; } = "";
    public string Result { get; set; } = "";
    public string Classification { get; set; } = "";
    public string ChangedAchievements { get; set; } = "";
    public string BeforeRequirements { get; set; } = "";
    public string AfterRequirements { get; set; } = "";
    public string FullDiff { get; set; } = "";
    public string BeforeState { get; set; } = "";
    public string AfterState { get; set; } = "";
    public string Status { get; set; } = "";

    public static ResearchRangeAttemptRow From(AchievementMappingResearchEntry entry, string status) =>
        new()
        {
            ProgressionData = entry.ProgressionData,
            Achievement = $"{entry.AchievementId} - {entry.AchievementName}",
            Result = entry.Result,
            Classification = entry.CandidateClassification,
            ChangedAchievements = entry.ChangedAchievementsSummary,
            BeforeRequirements = entry.ChangedAchievements.Count == 0
                ? entry.BeforeRequirementsSummary
                : string.Join("; ", entry.ChangedAchievements.Select(change =>
                    $"{change.AchievementId}: {change.BeforeRequirementsSummary}")),
            AfterRequirements = entry.ChangedAchievements.Count == 0
                ? entry.AfterRequirementsSummary
                : string.Join("; ", entry.ChangedAchievements.Select(change =>
                    $"{change.AchievementId}: {change.AfterRequirementsSummary}")),
            FullDiff = entry.FullDiffSummary,
            BeforeState = entry.BeforeState ?? "<none>",
            AfterState = entry.AfterState ?? "<none>",
            Status = status
        };
}
