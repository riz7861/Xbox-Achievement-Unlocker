using System.IO;
using Newtonsoft.Json;

namespace XAU.Services;

public static class QuantumBreakMappingResearchStore
{
    private static readonly object Sync = new();
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XAU",
        "Debug",
        "quantum_break_mapping_history.json");

    public static string FilePath => HistoryPath;

    public static IReadOnlyList<QuantumBreakMappingResearchEntry> Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(HistoryPath))
                    return [];

                var entries = JsonConvert.DeserializeObject<List<QuantumBreakMappingResearchEntry>>(
                    File.ReadAllText(HistoryPath)) ?? [];
                foreach (var entry in entries)
                {
                    entry.BeforeRequirements ??= [];
                    entry.AfterRequirements ??= [];
                    entry.ChangedAchievements ??= [];
                    QuantumBreakResearchClassifier.Apply(entry);
                }
                return entries;
            }
            catch
            {
                return [];
            }
        }
    }

    public static void Record(QuantumBreakMappingResearchEntry entry)
    {
        lock (Sync)
        {
            try
            {
                QuantumBreakResearchClassifier.Apply(entry);
                var entries = Load().ToList();
                entries.Add(entry);
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.WriteAllText(HistoryPath, JsonConvert.SerializeObject(entries, Formatting.Indented));
            }
            catch
            {
                // Research logging must never affect the explicit single-event test flow.
            }
        }
    }
}

public sealed class QuantumBreakMappingResearchEntry
{
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
    public int ProgressionData { get; set; }
    public string AchievementId { get; set; } = "";
    public string AchievementName { get; set; } = "";
    public string Result { get; set; } = "Unknown state change";
    public string CandidateClassification { get; set; } = QuantumBreakResearchClassifier.Unknown;
    public string AchievementClassification { get; set; } = QuantumBreakResearchClassifier.Unknown;
    public string? BeforeState { get; set; }
    public string? AfterState { get; set; }
    public string? BeforeUnlockTime { get; set; }
    public string? AfterUnlockTime { get; set; }
    public Dictionary<string, string?> BeforeRequirements { get; set; } = new();
    public Dictionary<string, string?> AfterRequirements { get; set; } = new();
    public List<QuantumBreakChangedAchievement> ChangedAchievements { get; set; } = new();
    public string ObservedChanges { get; set; } = "";

    [JsonIgnore]
    public string RecordedAt => RecordedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string BeforeRequirementsSummary => FormatRequirements(BeforeRequirements);

    [JsonIgnore]
    public string AfterRequirementsSummary => FormatRequirements(AfterRequirements);

    [JsonIgnore]
    public string ChangedAchievementsSummary => ChangedAchievements.Count == 0
        ? "<none>"
        : string.Join("; ", ChangedAchievements.Select(change =>
            $"{change.AchievementId} - {change.AchievementName}" +
            (change.IsSelectedAchievement ? " (selected)" : " (different)") +
            $" [{change.Classification}]"));

    [JsonIgnore]
    public string FullDiffSummary => ChangedAchievements.Count == 0
        ? ObservedChanges
        : string.Join(Environment.NewLine + Environment.NewLine, ChangedAchievements.Select(change => change.Details));

    [JsonIgnore]
    public string Details =>
        $"Recorded UTC: {RecordedAtUtc:O}{Environment.NewLine}" +
        $"ProgressionData: {ProgressionData}{Environment.NewLine}" +
        $"Achievement: {AchievementId} - {AchievementName}{Environment.NewLine}" +
        $"Result: {Result}{Environment.NewLine}" +
        $"Candidate classification: {CandidateClassification}{Environment.NewLine}" +
        $"Selected achievement classification: {AchievementClassification}{Environment.NewLine}" +
        $"State: {BeforeState ?? "<none>"} -> {AfterState ?? "<none>"}{Environment.NewLine}" +
        $"timeUnlocked: {BeforeUnlockTime ?? "<none>"} -> {AfterUnlockTime ?? "<none>"}{Environment.NewLine}" +
        $"Before requirements: {BeforeRequirementsSummary}{Environment.NewLine}" +
        $"After requirements: {AfterRequirementsSummary}{Environment.NewLine}" +
        $"Changed achievements: {ChangedAchievementsSummary}{Environment.NewLine}" +
        $"Full before/after diff:{Environment.NewLine}{FullDiffSummary}{Environment.NewLine}" +
        $"Observed changes:{Environment.NewLine}{ObservedChanges}";

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return new[]
        {
            ProgressionData.ToString(),
            AchievementId,
            AchievementName,
            Result,
            CandidateClassification,
            AchievementClassification,
            BeforeState,
            AfterState,
            BeforeUnlockTime,
            AfterUnlockTime,
            BeforeRequirementsSummary,
            AfterRequirementsSummary,
            ChangedAchievementsSummary,
            FullDiffSummary,
            ObservedChanges
        }.Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string FormatRequirements(Dictionary<string, string?> requirements) =>
        requirements.Count == 0
            ? "<none>"
            : string.Join(", ", requirements.Select(requirement => $"{requirement.Key}={requirement.Value ?? "<none>"}"));
}

public sealed class QuantumBreakChangedAchievement
{
    public string AchievementId { get; set; } = "";
    public string AchievementName { get; set; } = "";
    public bool IsSelectedAchievement { get; set; }
    public bool AchievementUnlocked { get; set; }
    public bool StateChanged { get; set; }
    public bool UnlockTimeChanged { get; set; }
    public bool RequirementChanged { get; set; }
    public string Classification { get; set; } = QuantumBreakResearchClassifier.Unknown;
    public string? BeforeState { get; set; }
    public string? AfterState { get; set; }
    public string? BeforeUnlockTime { get; set; }
    public string? AfterUnlockTime { get; set; }
    public Dictionary<string, string?> BeforeRequirements { get; set; } = new();
    public Dictionary<string, string?> AfterRequirements { get; set; } = new();
    public string RequirementDiff { get; set; } = "";

    [JsonIgnore]
    public string BeforeRequirementsSummary => FormatRequirements(BeforeRequirements);

    [JsonIgnore]
    public string AfterRequirementsSummary => FormatRequirements(AfterRequirements);

    [JsonIgnore]
    public string Details =>
        $"{AchievementId} - {AchievementName}{(IsSelectedAchievement ? " (selected)" : " (different achievement)")}{Environment.NewLine}" +
        $"Classification: {Classification}{Environment.NewLine}" +
        $"State: {BeforeState ?? "<none>"} -> {AfterState ?? "<none>"}{Environment.NewLine}" +
        $"timeUnlocked: {BeforeUnlockTime ?? "<none>"} -> {AfterUnlockTime ?? "<none>"}{Environment.NewLine}" +
        $"Before requirements: {BeforeRequirementsSummary}{Environment.NewLine}" +
        $"After requirements: {AfterRequirementsSummary}{Environment.NewLine}" +
        $"Requirement changes: {(string.IsNullOrWhiteSpace(RequirementDiff) ? "<none>" : RequirementDiff)}";

    private static string FormatRequirements(Dictionary<string, string?> requirements) =>
        requirements.Count == 0
            ? "<none>"
            : string.Join(", ", requirements.Select(requirement => $"{requirement.Key}={requirement.Value ?? "<none>"}"));
}

public static class QuantumBreakResearchClassifier
{
    public const string DirectUnlockMapping = "Direct Unlock Mapping";
    public const string DirectProgressionMapping = "Direct Progression Mapping";
    public const string CounterBasedAchievement = "Counter-Based Achievement";
    public const string FractionalAchievement = "Fractional/Percentage Achievement";
    public const string Unknown = "Unknown";

    private static readonly IReadOnlyDictionary<string, string> KnownAchievements =
        new Dictionary<string, string>
        {
            ["10"] = DirectUnlockMapping,
            ["11"] = DirectUnlockMapping,
            ["19"] = CounterBasedAchievement,
            ["28"] = FractionalAchievement,
            ["29"] = FractionalAchievement,
            ["47"] = FractionalAchievement,
            ["48"] = FractionalAchievement,
            ["49"] = DirectUnlockMapping,
            ["52"] = DirectUnlockMapping
        };

    private static readonly HashSet<int> KnownDirectUnlockCandidates = [1, 2, 22, 25];

    public static void Apply(QuantumBreakMappingResearchEntry entry)
    {
        foreach (var change in entry.ChangedAchievements)
            change.Classification = ClassifyAchievement(change.AchievementId, change);

        entry.CandidateClassification = ClassifyCandidate(entry);
        entry.AchievementClassification = ClassifyAchievement(
            entry.AchievementId,
            entry.ChangedAchievements.FirstOrDefault(change => change.AchievementId == entry.AchievementId));
    }

    public static string ClassifyCandidate(QuantumBreakMappingResearchEntry entry)
    {
        if (KnownDirectUnlockCandidates.Contains(entry.ProgressionData)
            || entry.ChangedAchievements.Any(change => change.AchievementUnlocked))
            return DirectUnlockMapping;

        return entry.ChangedAchievements.Any(change => change.RequirementChanged)
            ? DirectProgressionMapping
            : Unknown;
    }

    public static string ClassifyAchievement(
        string achievementId,
        IEnumerable<QuantumBreakMappingResearchEntry>? history = null)
    {
        if (KnownAchievements.TryGetValue(achievementId, out var known))
            return known;

        var changes = history?.SelectMany(entry => entry.ChangedAchievements)
            .Where(change => change.AchievementId == achievementId)
            .ToList() ?? [];
        if (changes.Any(change => change.AchievementUnlocked))
            return DirectUnlockMapping;
        if (changes.Any(change => change.RequirementChanged))
            return DirectProgressionMapping;
        return Unknown;
    }

    public static string ClassifyAchievement(string achievementId, QuantumBreakChangedAchievement? change)
    {
        if (KnownAchievements.TryGetValue(achievementId, out var known))
            return known;
        if (change?.AchievementUnlocked == true)
            return DirectUnlockMapping;
        if (change?.RequirementChanged == true)
            return DirectProgressionMapping;
        return Unknown;
    }
}
