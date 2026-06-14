using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using Wpf.Ui.Contracts;
using XAU.Services;

namespace XAU.ViewModels.Pages;

public partial class QuantumBreakResearchViewModel : ObservableObject, INavigationAware
{
    private const string QuantumBreakTitleId = "333628240";
    private const string QuantumBreakScid = "636a0100-392d-4116-9c4f-e0c513e2c350";

    [ObservableProperty] private string _eventTokenStatus = "Events token status has not been checked.";
    [ObservableProperty] private string _achievementStatus = "Loading Quantum Break achievements...";
    [ObservableProperty] private ObservableCollection<QuantumBreakResearchAchievement> _achievements = new();
    [ObservableProperty] private QuantumBreakResearchAchievement? _selectedAchievement;
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
    [ObservableProperty] private bool _watchAllQuantumBreakAchievements = true;
    [ObservableProperty] private bool _isRangeTesting;
    [ObservableProperty] private string _rangeStatus = "Range tester is idle.";
    [ObservableProperty] private string _rangeCurrentStatus = "No candidate is running.";
    [ObservableProperty] private string _proposedMapping = "";
    [ObservableProperty] private ObservableCollection<QuantumBreakRangeAttemptRow> _rangeAttempts = new();
    [ObservableProperty] private string _candidateHistorySummary = "No tested-candidate summary loaded.";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _historyStatus = "No mapping research history loaded.";
    [ObservableProperty] private ObservableCollection<QuantumBreakMappingResearchEntry> _entries = new();
    [ObservableProperty] private QuantumBreakMappingResearchEntry? _selectedEntry;
    [ObservableProperty] private ObservableCollection<QuantumBreakRequirementAnalysisRow> _requirementAnalysisRows = new();
    [ObservableProperty] private string _requirementAnalysisSummary = "Load Quantum Break achievements to analyse requirement structures.";

    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _rangeCancellation;
    private IReadOnlyList<QuantumBreakMappingResearchEntry> _allHistoryEntries = [];

    public QuantumBreakResearchViewModel(
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
    }

    public string TitleInfo => $"Quantum Break | Title ID {QuantumBreakTitleId} | SCID {QuantumBreakScid}";
    public string HistoryPath => QuantumBreakMappingResearchStore.FilePath;

    public async void OnNavigatedTo()
    {
        LoadHistory();
        UpdateEventTokenStatus();
        await LoadAchievements();
    }

    public void OnNavigatedFrom() => _rangeCancellation?.Cancel();

    partial void OnSearchTextChanged(string value) => LoadHistory();

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
            WatchAllQuantumBreakAchievements = false;
        else if (!WatchAllQuantumBreakAchievements)
            WatchAllQuantumBreakAchievements = true;
    }

    partial void OnWatchAllQuantumBreakAchievementsChanged(bool value)
    {
        if (value)
            WatchSelectedAchievementOnly = false;
        else if (!WatchSelectedAchievementOnly)
            WatchSelectedAchievementOnly = true;
    }

    [RelayCommand(CanExecute = nameof(CanRunRequest))]
    private async Task RefreshAchievements() => await LoadAchievements();

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

    [RelayCommand(CanExecute = nameof(CanRunRequest))]
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
                    Title = "Send one Quantum Break test event?",
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
            QuantumBreakMappingResearchStore.Record(entry);
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
                QuantumBreakMappingResearchStore.Record(entry);
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
                Title = "Start controlled Quantum Break range test?",
                Content =
                    $"Achievement: {selectedId} - {selectedName}\n" +
                    $"Candidates: {start} through {end}\n" +
                    $"Maximum attempts: {maxAttempts}\n" +
                    $"Delay between attempts: {delaySeconds} second(s)\n\n" +
                    $"Watch scope: {(WatchAllQuantumBreakAchievements ? "all Quantum Break achievements" : "selected achievement only")}\n\n" +
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

                RangeAttempts.Add(QuantumBreakRangeAttemptRow.From(result.Entry, result.Status));
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
        ShowCopied("Test Result Copied", "The visible Quantum Break test result was copied.");
    }

    [RelayCommand]
    private void RefreshHistory() => LoadHistory();

    [RelayCommand]
    private void CopySelectedEntry()
    {
        if (SelectedEntry == null)
            return;

        Clipboard.SetText(SelectedEntry.Details);
        ShowCopied("Research Record Copied", "The selected Quantum Break mapping research record was copied.");
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
        ShowCopied("Structure Analysis Copied", "The Quantum Break achievement structure analysis was copied.");
    }

    private bool CanRunRequest() => !IsSending && !IsRangeTesting;

    private bool CanStartRangeTest() => !IsSending && !IsRangeTesting;

    private bool CanStopRangeTest() => IsRangeTesting;

    private async Task LoadAchievements()
    {
        IsSending = true;
        try
        {
            UpdateEventTokenStatus();
            var response = await FetchAchievements();
            if (response == null)
            {
                AchievementStatus = "Quantum Break achievements could not be loaded. Ensure Xbox authentication is connected.";
                Achievements.Clear();
                ClearRequirementAnalysis();
                return;
            }

            ApplyAchievements(response, SelectedAchievement?.Id);
            AchievementStatus = $"Loaded {Achievements.Count} Quantum Break achievement(s) from Xbox.";
        }
        catch (Exception ex)
        {
            AchievementStatus = $"Quantum Break achievement load failed: {ex.Message}";
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
        var api = new XboxRestAPI(HomeViewModel.XAUTH);
        return await api.GetAchievementsForTitleAsync(HomeViewModel.XUIDOnly, QuantumBreakTitleId);
    }

    private void ApplyAchievements(AchievementsResponse response, string? selectedId)
    {
        var mappings = LoadQuantumBreakMappings();
        Achievements = new ObservableCollection<QuantumBreakResearchAchievement>(
            response.achievements.Select(achievement =>
            {
                var requirements = achievement.progression?.requirements ?? [];
                var mapping = mappings?[achievement.id] as JObject;
                return new QuantumBreakResearchAchievement
                {
                    Id = achievement.id,
                    Name = achievement.name,
                    State = achievement.progressState,
                    MappingStatus = FormatMappingStatus(mapping),
                    Classification = QuantumBreakResearchClassifier.ClassifyAchievement(achievement.id, _allHistoryEntries),
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
            var classification = QuantumBreakResearchClassifier.ClassifyAchievement(achievement.id, _allHistoryEntries);
            var isPriorityUnsupported = priorityUnsupportedIds.Contains(achievement.id)
                && mappingStatus.StartsWith("Unsupported", StringComparison.Ordinal)
                && !string.Equals(achievement.progressState, "Achieved", StringComparison.OrdinalIgnoreCase);
            var requirements = achievement.progression?.requirements ?? [];
            return requirements.Count == 0
                ? [CreateRequirementAnalysisRow(achievement, null, mappingStatus, classification, isPriorityUnsupported)]
                : requirements.Select(requirement =>
                    CreateRequirementAnalysisRow(achievement, requirement, mappingStatus, classification, isPriorityUnsupported));
        }).OrderBy(row => int.TryParse(row.AchievementId, out var id) ? id : int.MaxValue)
            .ThenBy(row => row.RequirementId);

        RequirementAnalysisRows = new ObservableCollection<QuantumBreakRequirementAnalysisRow>(rows);
        UpdateRequirementAnalysisSummary();
    }

    private static QuantumBreakRequirementAnalysisRow CreateRequirementAnalysisRow(
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
        RequirementAnalysisSummary = "Load Quantum Break achievements to analyse requirement structures.";
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
        var knownCandidates = new HashSet<int> { 1, 2, 22, 25 };
        var knownHits = _allHistoryEntries
            .Where(entry => entry.ChangedAchievements.Count > 0
                || entry.Result is "Progress changed" or "Achievement unlocked"
                || knownCandidates.Contains(entry.ProgressionData))
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

    private static string FormatAnalysisGroups(IEnumerable<IGrouping<string, QuantumBreakRequirementAnalysisRow>> groups) =>
        string.Join(", ", groups.OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));

    private static string FormatAnalysisLines(IReadOnlyCollection<string> lines) =>
        lines.Count == 0 ? "<none>" : string.Join(Environment.NewLine, lines);

    private static JObject? LoadQuantumBreakMappings()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "Events", "Data.json");
            return JObject.Parse(File.ReadAllText(path))[QuantumBreakTitleId]?["Achievements"] as JObject;
        }
        catch
        {
            return null;
        }
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
        if (SelectedAchievement == null)
        {
            TestStatus = "Select a Quantum Break achievement first.";
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

    private static string BuildPayload(int candidate, bool forSend)
    {
        var templatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "Events", "333628240.json");
        var requestBody = File.ReadAllText(templatePath)
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
            QuantumBreakMappingResearchStore.Record(entry);
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

    private static RangeCandidateResult PersistRangeFailure(
        int candidate,
        string selectedId,
        string selectedName,
        Dictionary<string, ResearchSnapshot> before,
        string status)
    {
        var entry = CreateHistoryEntry(candidate, selectedId, selectedName, before, [], status, true);
        QuantumBreakMappingResearchStore.Record(entry);
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
            RangeStatus = "Select a Quantum Break achievement first.";
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
        var watchedChanges = WatchAllQuantumBreakAchievements
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

    private static SnapshotChangeSummary AnalyzeSnapshotChanges(
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

            var change = new QuantumBreakChangedAchievement
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
            change.Classification = QuantumBreakResearchClassifier.ClassifyAchievement(achievementId, change);
            result.Changes.Add(change);
        }

        return result;
    }

    private static string FormatAttemptStatus(int candidate, QuantumBreakMappingResearchEntry entry) =>
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

    private static string CompareSnapshots(
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

    private static QuantumBreakMappingResearchEntry CreateHistoryEntry(
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

        return new QuantumBreakMappingResearchEntry
        {
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
        var allEntries = QuantumBreakMappingResearchStore.Load()
            .OrderByDescending(entry => entry.RecordedAtUtc)
            .ToList();
        _allHistoryEntries = allEntries;
        RefreshAchievementClassifications();
        var filtered = allEntries.Where(entry => entry.Matches(SearchText)).ToList();

        Entries = new ObservableCollection<QuantumBreakMappingResearchEntry>(filtered);
        SelectedEntry = selectedRecordedAt.HasValue
            ? Entries.FirstOrDefault(entry => entry.RecordedAtUtc == selectedRecordedAt.Value)
            : Entries.FirstOrDefault();
        HistoryStatus = $"Showing {filtered.Count} of {allEntries.Count} persisted research record(s).";
        CandidateHistorySummary = BuildCandidateHistorySummary(allEntries);
        UpdateRequirementAnalysisSummary();
    }

    private void RefreshAchievementClassifications()
    {
        var selectedId = SelectedAchievement?.Id;
        foreach (var achievement in Achievements)
            achievement.Classification = QuantumBreakResearchClassifier.ClassifyAchievement(
                achievement.Id, _allHistoryEntries);
        if (Achievements.Count > 0)
        {
            Achievements = new ObservableCollection<QuantumBreakResearchAchievement>(Achievements);
            SelectedAchievement = Achievements.FirstOrDefault(achievement => achievement.Id == selectedId)
                ?? Achievements.FirstOrDefault();
        }

        foreach (var row in RequirementAnalysisRows)
            row.Classification = QuantumBreakResearchClassifier.ClassifyAchievement(
                row.AchievementId, _allHistoryEntries);
        if (RequirementAnalysisRows.Count > 0)
            RequirementAnalysisRows = new ObservableCollection<QuantumBreakRequirementAnalysisRow>(
                RequirementAnalysisRows);
    }

    private static string BuildCandidateHistorySummary(IReadOnlyCollection<QuantumBreakMappingResearchEntry> entries)
    {
        var tested = entries.Select(entry => entry.ProgressionData).Distinct().OrderBy(value => value).ToList();
        if (tested.Count == 0)
            return "Known hits: <none>\nKnown misses: <none>\nUntested ranges: <none>";

        var knownCandidates = new HashSet<int> { 1, 2, 22, 25 };
        var knownHits = tested.Where(knownCandidates.Contains).ToList();
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

        return $"Known hits (1, 2, 22, 25 present in history): {FormatCandidateValues(knownHits)}{Environment.NewLine}" +
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
        public required QuantumBreakMappingResearchEntry Entry { get; init; }
        public string Status { get; init; } = "";
        public bool RequestFailed { get; init; }
        public bool Cancelled { get; set; }
        public SnapshotChangeSummary ChangeSummary { get; init; } = new();
        public bool SelectedAchievementChanged => ChangeSummary.SelectedAchievementChanged;
    }

    private sealed class SnapshotChangeSummary
    {
        public List<QuantumBreakChangedAchievement> Changes { get; } = new();
        public bool AchievementUnlocked => Changes.Any(change => change.AchievementUnlocked);
        public bool StateChanged => Changes.Any(change => change.StateChanged);
        public bool RequirementChanged => Changes.Any(change => change.RequirementChanged);
        public bool SelectedAchievementChanged => Changes.Any(change => change.IsSelectedAchievement);
    }
}

public sealed class QuantumBreakResearchAchievement
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string MappingStatus { get; set; } = "";
    public string Classification { get; set; } = QuantumBreakResearchClassifier.Unknown;
    public string Current { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class QuantumBreakRequirementAnalysisRow
{
    public string AchievementId { get; set; } = "";
    public string AchievementName { get; set; } = "";
    public string State { get; set; } = "";
    public string MappingStatus { get; set; } = "";
    public string Classification { get; set; } = QuantumBreakResearchClassifier.Unknown;
    public string RequirementId { get; set; } = "";
    public string Current { get; set; } = "";
    public string Target { get; set; } = "";
    public string OperationType { get; set; } = "";
    public string ValueType { get; set; } = "";
    public string RuleParticipationType { get; set; } = "";
    public string Shape { get; set; } = "";
    public bool IsPriorityUnsupported { get; set; }
}

public sealed class QuantumBreakRangeAttemptRow
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

    public static QuantumBreakRangeAttemptRow From(QuantumBreakMappingResearchEntry entry, string status) =>
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
