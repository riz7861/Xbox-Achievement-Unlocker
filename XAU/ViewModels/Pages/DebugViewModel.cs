using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Wpf.Ui.Controls;
using Newtonsoft.Json;
using Wpf.Ui.Contracts;

namespace XAU.ViewModels.Pages
{
    public partial class DebugViewModel : ObservableObject, INavigationAware
    {
        private const string QuantumBreakPackageName = "Microsoft.QuantumBreak_8wekyb3d8bbwe";
        private const string QuantumBreakScidFolderSuffix = "_636A0100392D41169C4FE0C513E2C350";
        private const int MaxDisplayedByteRangesPerFile = 1000;
        private const int MaxLiveWatchRows = 500;
        private const long LargeSaveBlobBytes = 1024 * 1024;

        private bool _isInitialized = false;
        private SaveSnapshot? _snapshotA;
        private SaveSnapshot? _snapshotB;
        private FileSystemWatcher? _quantumBreakSaveWatcher;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _watchDebounce = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _knownWatchFileSizes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _knownContainerTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SaveWatchRow> _quantumBreakLiveChangeHistory = new();
        private CancellationTokenSource? _savegamesVersionDebounce;
        private SavegamesVersion? _previousSavegamesVersion;
        private SavegamesVersion? _currentSavegamesVersion;
        private bool _hasSavegamesPayloadChanges;
        private bool _hasSavegamesMetadataOnlyChanges;

        [ObservableProperty]
        private string _quantumBreakWgsFolder = "";

        [ObservableProperty]
        private string _quantumBreakSaveDiffStatus = "Read-only research helper. Snapshots are kept in memory.";

        [ObservableProperty]
        private string _snapshotASummary = "Snapshot A: not taken";

        [ObservableProperty]
        private string _snapshotBSummary = "Snapshot B: not taken";

        [ObservableProperty]
        private string _quantumBreakSaveDiffReport = "";

        [ObservableProperty]
        private bool _isQuantumBreakSaveDiffBusy;

        [ObservableProperty]
        private ObservableCollection<SaveDiffRow> _quantumBreakSaveDiffRows = new();

        [ObservableProperty]
        private ObservableCollection<SaveWatchRow> _quantumBreakLiveChanges = new();

        public IReadOnlyList<string> QuantumBreakLiveFilters { get; } =
            ["All changes", "Savegames only", "Preferences only"];

        [ObservableProperty]
        private string _selectedQuantumBreakLiveFilter = "Savegames only";

        [ObservableProperty]
        private bool _hideQuantumBreakWgsMetadata = true;

        [ObservableProperty]
        private string _quantumBreakLiveWatchStatus = "Read-only live watcher stopped.";

        [ObservableProperty]
        private string _quantumBreakLiveChangeLog = "";

        [ObservableProperty]
        private bool _isQuantumBreakWatchRunning;

        [ObservableProperty]
        private string _lastQuantumBreakSavegamesChangeTime = "None";

        [ObservableProperty]
        private string _lastQuantumBreakPreferencesChangeTime = "None";

        [ObservableProperty]
        private int _quantumBreakSavegamesChangeCount;

        [ObservableProperty]
        private int _quantumBreakPreferencesChangeCount;

        [ObservableProperty]
        private string _quantumBreakWatchSessionSummary = "No changes captured in this watch session.";

        [ObservableProperty]
        private string _quantumBreakSavegamesDiffStatus = "No Savegames versions captured.";

        [ObservableProperty]
        private string _quantumBreakSavegamesDiffReport = "";

        [ObservableProperty]
        private ObservableCollection<SaveDiffRow> _quantumBreakSavegamesDiffRows = new();

        public void OnNavigatedTo()
        {
            if (!_isInitialized)
                InitializeViewModel();
        }

        public void OnNavigatedFrom()
        {
            StopQuantumBreakSaveWatchCore();
            QuantumBreakLiveWatchStatus = "Read-only live watcher stopped when leaving Debug.";
        }

        private void InitializeViewModel()
        {
            QuantumBreakWgsFolder = DetectQuantumBreakWgsFolder();
            QuantumBreakSaveDiffStatus = Directory.Exists(QuantumBreakWgsFolder)
                ? "Quantum Break WGS folder detected. Read-only research helper ready."
                : "Quantum Break WGS folder was not found.";
            _isInitialized = true;
        }
        public DebugViewModel(ISnackbarService snackbarService, IContentDialogService contentDialogService)
        {
            _snackbarService = snackbarService;
            _contentDialogService = contentDialogService;
        }
        private readonly ISnackbarService _snackbarService;
        private TimeSpan _snackbarDuration = TimeSpan.FromSeconds(2);
        private readonly IContentDialogService _contentDialogService;

        [RelayCommand]
        private async Task StartQuantumBreakSaveWatch()
        {
            StopQuantumBreakSaveWatchCore();
            QuantumBreakWgsFolder = DetectQuantumBreakWgsFolder();

            if (!Directory.Exists(QuantumBreakWgsFolder))
            {
                QuantumBreakLiveWatchStatus = "Quantum Break WGS folder was not found.";
                return;
            }

            _quantumBreakLiveChangeHistory.Clear();
            QuantumBreakLiveChanges.Clear();
            QuantumBreakLiveChangeLog = "";
            LastQuantumBreakSavegamesChangeTime = "None";
            LastQuantumBreakPreferencesChangeTime = "None";
            QuantumBreakSavegamesChangeCount = 0;
            QuantumBreakPreferencesChangeCount = 0;
            _hasSavegamesPayloadChanges = false;
            _hasSavegamesMetadataOnlyChanges = false;
            UpdateWatchSessionSummary();
            ApplyQuantumBreakLiveFilter();
            InitializeWatchCaches(QuantumBreakWgsFolder);
            QuantumBreakSavegamesDiffRows.Clear();
            QuantumBreakSavegamesDiffReport = "";
            _previousSavegamesVersion = null;
            try
            {
                _currentSavegamesVersion = await Task.Run(() => CaptureSavegamesVersion(QuantumBreakWgsFolder));
                QuantumBreakSavegamesDiffStatus = _currentSavegamesVersion is null
                    ? "Savegames container was not found."
                    : $"Savegames baseline captured: {_currentSavegamesVersion.ContainerFileName}, hash={_currentSavegamesVersion.HashPrefix}.";
            }
            catch (Exception ex)
            {
                _currentSavegamesVersion = null;
                QuantumBreakSavegamesDiffStatus = $"Could not capture Savegames baseline: {ex.Message}";
            }

            _quantumBreakSaveWatcher = new FileSystemWatcher(QuantumBreakWgsFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024
            };
            _quantumBreakSaveWatcher.Changed += OnQuantumBreakSaveChanged;
            _quantumBreakSaveWatcher.Created += OnQuantumBreakSaveChanged;
            _quantumBreakSaveWatcher.Deleted += OnQuantumBreakSaveChanged;
            _quantumBreakSaveWatcher.Renamed += OnQuantumBreakSaveRenamed;
            _quantumBreakSaveWatcher.Error += OnQuantumBreakSaveWatchError;
            _quantumBreakSaveWatcher.EnableRaisingEvents = true;

            IsQuantumBreakWatchRunning = true;
            QuantumBreakLiveWatchStatus = "Watching Quantum Break WGS files (read-only research).";
        }

        [RelayCommand]
        private void StopQuantumBreakSaveWatch()
        {
            StopQuantumBreakSaveWatchCore();
            QuantumBreakLiveWatchStatus = "Read-only live watcher stopped.";
        }

        private void StopQuantumBreakSaveWatchCore()
        {
            IsQuantumBreakWatchRunning = false;

            if (_quantumBreakSaveWatcher is not null)
            {
                _quantumBreakSaveWatcher.EnableRaisingEvents = false;
                _quantumBreakSaveWatcher.Dispose();
                _quantumBreakSaveWatcher = null;
            }

            foreach (var pending in _watchDebounce.Values)
                pending.Cancel();
            _watchDebounce.Clear();

            _savegamesVersionDebounce?.Cancel();
            _savegamesVersionDebounce = null;
        }

        partial void OnSelectedQuantumBreakLiveFilterChanged(string value)
        {
            ApplyQuantumBreakLiveFilter();
        }

        partial void OnHideQuantumBreakWgsMetadataChanged(bool value)
        {
            ApplyQuantumBreakLiveFilter();
        }

        [RelayCommand]
        private void CopyQuantumBreakLiveChangeLog()
        {
            if (string.IsNullOrWhiteSpace(QuantumBreakLiveChangeLog))
            {
                QuantumBreakLiveWatchStatus = "No live changes have been captured yet.";
                return;
            }

            Clipboard.SetText(QuantumBreakLiveChangeLog);
            QuantumBreakLiveWatchStatus = "Live change log copied to clipboard.";
        }

        [RelayCommand]
        private async Task ExportQuantumBreakFilteredLiveChangeLog()
        {
            if (string.IsNullOrWhiteSpace(QuantumBreakLiveChangeLog))
            {
                QuantumBreakLiveWatchStatus = "No changes are visible in the current filtered view.";
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"QuantumBreak-filtered-live-changes-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExt = ".txt",
                Filter = "Text report (*.txt)|*.txt|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            await File.WriteAllTextAsync(dialog.FileName, QuantumBreakLiveChangeLog);
            QuantumBreakLiveWatchStatus = $"Filtered live change report exported to {dialog.FileName}";
        }

        private void OnQuantumBreakSaveChanged(object sender, FileSystemEventArgs e)
        {
            QueueQuantumBreakSaveChange(e.FullPath, e.ChangeType.ToString());
        }

        private void OnQuantumBreakSaveRenamed(object sender, RenamedEventArgs e)
        {
            var oldPath = Path.GetRelativePath(QuantumBreakWgsFolder, e.OldFullPath);
            QueueQuantumBreakSaveChange(e.FullPath, $"Renamed from {oldPath}");
        }

        private void OnQuantumBreakSaveWatchError(object sender, ErrorEventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                QuantumBreakLiveWatchStatus = $"Live watcher error: {e.GetException().Message}";
            });
        }

        private void QueueQuantumBreakSaveChange(string fullPath, string changeType)
        {
            var cancellation = new CancellationTokenSource();
            _watchDebounce.AddOrUpdate(
                fullPath,
                cancellation,
                (_, existing) =>
                {
                    existing.Cancel();
                    return cancellation;
                });

            _ = ProcessQuantumBreakSaveChangeAsync(fullPath, changeType, cancellation);
        }

        private async Task ProcessQuantumBreakSaveChangeAsync(string fullPath, string changeType, CancellationTokenSource cancellation)
        {
            try
            {
                await Task.Delay(750, cancellation.Token);
                var root = QuantumBreakWgsFolder;
                var relativePath = Path.GetRelativePath(root, fullPath);
                var container = ResolveKnownWgsContainer(root, relativePath);
                var previousSize = _knownWatchFileSizes.TryGetValue(fullPath, out var knownSize) ? knownSize : (long?)null;
                var row = await Task.Run(
                    () => CreateSaveWatchRow(root, fullPath, changeType, container, previousSize),
                    cancellation.Token);

                if (row.Size.HasValue && File.Exists(fullPath))
                    _knownWatchFileSizes[fullPath] = row.Size.Value;
                else
                    _knownWatchFileSizes.TryRemove(fullPath, out _);

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AddQuantumBreakLiveChange(row);
                    QuantumBreakLiveWatchStatus = $"Watching Quantum Break WGS files (read-only). Last change: {row.Timestamp}";
                });

                if (row.Container == "Savegames")
                    QueueSavegamesVersionComparison(root);
            }
            catch (OperationCanceledException)
            {
                // A newer event for this file replaced this pending observation.
            }
            catch (Exception ex)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    QuantumBreakLiveWatchStatus = $"Could not inspect live change: {ex.Message}";
                });
            }
            finally
            {
                if (_watchDebounce.TryGetValue(fullPath, out var current) && ReferenceEquals(current, cancellation))
                    _watchDebounce.TryRemove(fullPath, out _);
                cancellation.Dispose();
            }
        }

        private void QueueSavegamesVersionComparison(string root)
        {
            var cancellation = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _savegamesVersionDebounce, cancellation);
            previous?.Cancel();
            _ = CaptureAndCompareSavegamesVersionAsync(root, cancellation);
        }

        private async Task CaptureAndCompareSavegamesVersionAsync(string root, CancellationTokenSource cancellation)
        {
            try
            {
                await Task.Delay(1500, cancellation.Token);
                var version = await Task.Run(() => CaptureSavegamesVersion(root), cancellation.Token);
                if (!IsQuantumBreakWatchRunning || version is null || version.Hash == _currentSavegamesVersion?.Hash)
                    return;

                _previousSavegamesVersion = _currentSavegamesVersion;
                _currentSavegamesVersion = version;
                await CompareLastTwoSavegamesVersionsCoreAsync();
            }
            catch (OperationCanceledException)
            {
                // A newer Savegames change replaced this pending version capture.
            }
            catch (Exception ex)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    QuantumBreakSavegamesDiffStatus = $"Could not capture Savegames version: {ex.Message}";
                });
            }
            finally
            {
                if (ReferenceEquals(_savegamesVersionDebounce, cancellation))
                    Interlocked.CompareExchange(ref _savegamesVersionDebounce, null, cancellation);
                cancellation.Dispose();
            }
        }

        [RelayCommand]
        private async Task CompareLastTwoSavegamesVersions()
        {
            await CompareLastTwoSavegamesVersionsCoreAsync();
        }

        private async Task CompareLastTwoSavegamesVersionsCoreAsync()
        {
            var previous = _previousSavegamesVersion;
            var current = _currentSavegamesVersion;
            if (previous is null || current is null)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    QuantumBreakSavegamesDiffStatus = "Two Savegames versions have not been captured yet.";
                });
                return;
            }

            var result = await Task.Run(() => CompareSavegamesVersions(previous, current));
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                QuantumBreakSavegamesDiffRows.Clear();
                foreach (var row in result.Rows)
                    QuantumBreakSavegamesDiffRows.Add(row);

                QuantumBreakSavegamesDiffReport = result.Report;
                QuantumBreakSavegamesDiffStatus =
                    $"{previous.ContainerFileName} ({previous.HashPrefix}) -> {current.ContainerFileName} ({current.HashPrefix}): " +
                    $"{result.Rows.Count} changed files, {result.Rows.Sum(row => row.TotalBytesChanged):N0} changed bytes.";
            });
        }

        [RelayCommand]
        private async Task ExportQuantumBreakSavegamesDiffReport()
        {
            if (string.IsNullOrWhiteSpace(QuantumBreakSavegamesDiffReport))
            {
                QuantumBreakSavegamesDiffStatus = "Compare two Savegames versions before exporting.";
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"QuantumBreak-savegames-version-diff-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExt = ".txt",
                Filter = "Text report (*.txt)|*.txt|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            await File.WriteAllTextAsync(dialog.FileName, QuantumBreakSavegamesDiffReport);
            QuantumBreakSavegamesDiffStatus = $"Savegames version diff exported to {dialog.FileName}";
        }

        private static SaveWatchRow CreateSaveWatchRow(
            string root,
            string fullPath,
            string changeType,
            string container,
            long? previousSize)
        {
            var relativePath = Path.GetRelativePath(root, fullPath);
            var observedAt = DateTime.UtcNow;
            var fileName = Path.GetFileName(relativePath);
            var isLifecycle = changeType.Contains("Created", StringComparison.OrdinalIgnoreCase)
                              || changeType.Contains("Deleted", StringComparison.OrdinalIgnoreCase)
                              || changeType.Contains("Renamed", StringComparison.OrdinalIgnoreCase);
            var isContainerMetadata = fileName.StartsWith("container.", StringComparison.OrdinalIgnoreCase);

            if (!File.Exists(fullPath))
            {
                var signal = ClassifyWatchSignal(container, isLifecycle, isContainerMetadata, previousSize);
                return new SaveWatchRow
                {
                    ChangeType = changeType,
                    RelativePath = relativePath,
                    Container = container,
                    Timestamp = $"{observedAt:yyyy-MM-dd HH:mm:ss.fff} UTC",
                    Size = previousSize,
                    Hash = "",
                    Signal = signal.Signal,
                    IsImportant = signal.IsImportant,
                    IsMetadataOnly = signal.IsMetadataOnly,
                    IsSavegamesPayloadChange = signal.IsSavegamesPayloadChange
                };
            }

            var content = ReadSettledFile(fullPath);
            var info = new FileInfo(fullPath);
            var currentSignal = ClassifyWatchSignal(container, isLifecycle, isContainerMetadata, info.Length);
            return new SaveWatchRow
            {
                ChangeType = changeType,
                RelativePath = relativePath,
                Container = container,
                Timestamp = $"{info.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss.fff} UTC",
                Size = info.Length,
                Hash = Convert.ToHexString(SHA256.HashData(content)),
                Signal = currentSignal.Signal,
                IsImportant = currentSignal.IsImportant,
                IsMetadataOnly = currentSignal.IsMetadataOnly,
                IsSavegamesPayloadChange = currentSignal.IsSavegamesPayloadChange
            };
        }

        private static WatchSignal ClassifyWatchSignal(
            string container,
            bool isLifecycle,
            bool isContainerMetadata,
            long? size)
        {
            if (container == "Savegames")
            {
                if (isContainerMetadata)
                {
                    return new WatchSignal(
                        isLifecycle ? "Savegames container lifecycle" : "Savegames metadata",
                        isLifecycle,
                        true,
                        false);
                }

                if (size >= LargeSaveBlobBytes)
                {
                    return new WatchSignal(
                        isLifecycle ? "Large gameplay payload lifecycle" : "Gameplay payload (slot_1 equivalent)",
                        true,
                        false,
                        true);
                }

                return new WatchSignal(
                    isLifecycle ? "Savegames payload lifecycle" : "Savegames payload",
                    isLifecycle,
                    false,
                    true);
            }

            return container switch
            {
                "Preferences" => new WatchSignal("Preferences", false, false, false),
                "WGS metadata" => new WatchSignal("WGS metadata", false, true, false),
                _ => new WatchSignal("Unclassified", false, false, false)
            };
        }

        private static byte[] ReadSettledFile(string path)
        {
            IOException? lastError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    return File.ReadAllBytes(path);
                }
                catch (IOException ex)
                {
                    lastError = ex;
                    Thread.Sleep(200);
                }
            }

            throw lastError ?? new IOException($"Could not read {path}");
        }

        private void InitializeWatchCaches(string root)
        {
            _knownWatchFileSizes.Clear();
            _knownContainerTypes.Clear();

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    _knownWatchFileSizes[path] = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    // The watcher can populate this entry after the active save operation settles.
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var relativeProbePath = Path.Combine(Path.GetFileName(directory), "_");
                var container = ResolveWgsContainer(root, relativeProbePath);
                if (container != "Unknown container")
                    _knownContainerTypes[directory] = container;
            }
        }

        private string ResolveKnownWgsContainer(string root, string relativePath)
        {
            var separator = relativePath.IndexOf(Path.DirectorySeparatorChar);
            if (separator < 0)
                return "WGS metadata";

            var containerFolder = Path.Combine(root, relativePath[..separator]);
            var resolved = ResolveWgsContainer(root, relativePath);
            if (resolved != "Unknown container")
            {
                _knownContainerTypes[containerFolder] = resolved;
                return resolved;
            }

            return _knownContainerTypes.TryGetValue(containerFolder, out var known)
                ? known
                : resolved;
        }

        private static string ResolveWgsContainer(string root, string relativePath)
        {
            var separator = relativePath.IndexOf(Path.DirectorySeparatorChar);
            if (separator < 0)
                return "WGS metadata";

            try
            {
                var containerFolder = Path.Combine(root, relativePath[..separator]);
                var metadataPath = Directory.EnumerateFiles(containerFolder, "container.*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (metadataPath is null)
                    return "Unknown container";

                var metadata = Encoding.Unicode.GetString(File.ReadAllBytes(metadataPath));
                if (metadata.Contains("preferences", StringComparison.OrdinalIgnoreCase))
                    return "Preferences";
                if (metadata.Contains("slot_", StringComparison.OrdinalIgnoreCase))
                    return "Savegames";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "Unknown container";
            }

            return "Unknown container";
        }

        private void AddQuantumBreakLiveChange(SaveWatchRow row)
        {
            _quantumBreakLiveChangeHistory.Insert(0, row);
            while (_quantumBreakLiveChangeHistory.Count > MaxLiveWatchRows)
                _quantumBreakLiveChangeHistory.RemoveAt(_quantumBreakLiveChangeHistory.Count - 1);

            if (row.Container == "Savegames")
            {
                QuantumBreakSavegamesChangeCount++;
                LastQuantumBreakSavegamesChangeTime = row.Timestamp;
                _hasSavegamesPayloadChanges |= row.IsSavegamesPayloadChange;
                _hasSavegamesMetadataOnlyChanges |= row.IsMetadataOnly;
            }
            else if (row.Container == "Preferences")
            {
                QuantumBreakPreferencesChangeCount++;
                LastQuantumBreakPreferencesChangeTime = row.Timestamp;
            }

            UpdateWatchSessionSummary();
            ApplyQuantumBreakLiveFilter();
        }

        private void ApplyQuantumBreakLiveFilter()
        {
            var filtered = _quantumBreakLiveChangeHistory.Where(row =>
            {
                if (HideQuantumBreakWgsMetadata && row.Container == "WGS metadata")
                    return false;

                return SelectedQuantumBreakLiveFilter switch
                {
                    "Savegames only" => row.Container == "Savegames",
                    "Preferences only" => row.Container == "Preferences",
                    _ => true
                };
            }).ToList();

            QuantumBreakLiveChanges.Clear();
            foreach (var row in filtered)
                QuantumBreakLiveChanges.Add(row);

            QuantumBreakLiveChangeLog = BuildLiveChangeLog(
                filtered,
                SelectedQuantumBreakLiveFilter,
                HideQuantumBreakWgsMetadata,
                QuantumBreakWatchSessionSummary,
                QuantumBreakSavegamesChangeCount,
                QuantumBreakPreferencesChangeCount);
        }

        private void UpdateWatchSessionSummary()
        {
            var preferencesOnly = QuantumBreakPreferencesChangeCount > 0 && QuantumBreakSavegamesChangeCount == 0;
            QuantumBreakWatchSessionSummary =
                $"Savegames payload changes: {YesNo(_hasSavegamesPayloadChanges)} | " +
                $"Savegames metadata-only changes: {YesNo(_hasSavegamesMetadataOnlyChanges)} | " +
                $"Preferences-only changes: {YesNo(preferencesOnly)}";

            static string YesNo(bool value) => value ? "Yes" : "No";
        }

        private static SavegamesVersion? CaptureSavegamesVersion(string root)
        {
            var savegamesFolder = Directory.EnumerateDirectories(root)
                .FirstOrDefault(path =>
                {
                    var metadataPath = Directory.EnumerateFiles(path, "container.*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();
                    if (metadataPath is null)
                        return false;

                    try
                    {
                        return Encoding.Unicode.GetString(ReadSettledFile(metadataPath))
                            .Contains("slot_", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                });

            if (savegamesFolder is null)
                return null;

            var files = new Dictionary<string, SaveFileSnapshot>(StringComparer.OrdinalIgnoreCase);
            using var combinedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var path in Directory.EnumerateFiles(savegamesFolder, "*", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                var content = ReadSettledFile(path);
                var info = new FileInfo(path);
                var relativePath = Path.GetFileName(path);
                combinedHash.AppendData(Encoding.UTF8.GetBytes(relativePath));
                combinedHash.AppendData([0]);
                combinedHash.AppendData(content);
                files[relativePath] = new SaveFileSnapshot(
                    relativePath,
                    content.LongLength,
                    info.LastWriteTimeUtc,
                    Convert.ToHexString(SHA256.HashData(content)),
                    content);
            }

            var containerFileName = files.Keys
                .Where(path => path.StartsWith("container.", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .LastOrDefault() ?? "<container metadata missing>";
            var snapshot = new SaveSnapshot(DateTime.UtcNow, savegamesFolder, files);
            return new SavegamesVersion(containerFileName, Convert.ToHexString(combinedHash.GetHashAndReset()), snapshot);
        }

        private static SaveComparison CompareSavegamesVersions(SavegamesVersion previous, SavegamesVersion current)
        {
            var rows = CompareSnapshots(previous.Snapshot, current.Snapshot).Rows;
            var report = new StringBuilder()
                .AppendLine("Quantum Break Savegames Version Diff (read-only research)")
                .AppendLine($"Savegames folder: {current.Snapshot.RootPath}")
                .AppendLine($"Previous: {previous.ContainerFileName}, captured={previous.Snapshot.CapturedAtUtc:O}")
                .AppendLine($"Previous SHA-256: {previous.Hash}")
                .AppendLine($"Current: {current.ContainerFileName}, captured={current.Snapshot.CapturedAtUtc:O}")
                .AppendLine($"Current SHA-256: {current.Hash}")
                .AppendLine($"Changed files: {rows.Count}")
                .AppendLine($"Total changed bytes: {rows.Sum(row => row.TotalBytesChanged):N0}")
                .AppendLine();

            foreach (var row in rows)
            {
                report.AppendLine($"[{row.ChangeType}] {row.RelativePath}")
                    .AppendLine($"  Old SHA-256: {(string.IsNullOrEmpty(row.HashA) ? "<not present>" : row.HashA)}")
                    .AppendLine($"  New SHA-256: {(string.IsNullOrEmpty(row.HashB) ? "<not present>" : row.HashB)}")
                    .AppendLine($"  Old size: {(row.SizeA.HasValue ? $"{row.SizeA:N0} bytes" : "<not present>")}")
                    .AppendLine($"  New size: {(row.SizeB.HasValue ? $"{row.SizeB:N0} bytes" : "<not present>")}")
                    .AppendLine($"  Changed bytes: {row.TotalBytesChanged:N0}")
                    .AppendLine($"  Changed byte ranges: {row.ChangedByteRanges}")
                    .AppendLine();
            }

            return new SaveComparison(rows, report.ToString());
        }

        private static string BuildLiveChangeLog(
            IReadOnlyCollection<SaveWatchRow> rows,
            string filter,
            bool metadataHidden,
            string sessionSummary,
            int savegamesCount,
            int preferencesCount)
        {
            var log = new StringBuilder()
                .AppendLine("Quantum Break WGS Live Change Log (read-only research)")
                .AppendLine($"Current filtered view: {filter}")
                .AppendLine($"WGS metadata hidden: {(metadataHidden ? "Yes" : "No")}")
                .AppendLine($"Visible changes: {rows.Count}")
                .AppendLine($"Session Savegames changes: {savegamesCount}")
                .AppendLine($"Session Preferences changes: {preferencesCount}")
                .AppendLine(sessionSummary)
                .AppendLine();

            foreach (var row in rows.Reverse())
            {
                log.AppendLine(
                    $"{row.Timestamp} | {row.ChangeType} | {row.Container} | {row.Signal} | {row.RelativePath} | " +
                    $"size={row.SizeDisplay} | sha256={row.HashDisplay}");
            }

            return log.ToString();
        }

        [RelayCommand]
        private async Task TakeQuantumBreakSnapshotA()
        {
            await TakeQuantumBreakSnapshotAsync(true);
        }

        [RelayCommand]
        private async Task TakeQuantumBreakSnapshotB()
        {
            await TakeQuantumBreakSnapshotAsync(false);
        }

        private async Task TakeQuantumBreakSnapshotAsync(bool isSnapshotA)
        {
            if (IsQuantumBreakSaveDiffBusy)
                return;

            QuantumBreakWgsFolder = DetectQuantumBreakWgsFolder();
            if (!Directory.Exists(QuantumBreakWgsFolder))
            {
                QuantumBreakSaveDiffStatus = "Quantum Break WGS folder was not found.";
                return;
            }

            IsQuantumBreakSaveDiffBusy = true;
            QuantumBreakSaveDiffStatus = $"Taking snapshot {(isSnapshotA ? "A" : "B")} (read-only)...";

            try
            {
                var snapshot = await Task.Run(() => CaptureSaveSnapshot(QuantumBreakWgsFolder));
                var summary = $"{(isSnapshotA ? "Snapshot A" : "Snapshot B")}: {snapshot.Files.Count} files, " +
                              $"{snapshot.Files.Values.Sum(file => file.Size):N0} bytes, " +
                              $"{snapshot.CapturedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";

                if (isSnapshotA)
                {
                    _snapshotA = snapshot;
                    SnapshotASummary = summary;
                }
                else
                {
                    _snapshotB = snapshot;
                    SnapshotBSummary = summary;
                }

                QuantumBreakSaveDiffRows.Clear();
                QuantumBreakSaveDiffReport = "";
                QuantumBreakSaveDiffStatus = $"{summary}. No save data was modified.";
            }
            catch (Exception ex)
            {
                QuantumBreakSaveDiffStatus = $"Snapshot failed: {ex.Message}";
            }
            finally
            {
                IsQuantumBreakSaveDiffBusy = false;
            }
        }

        [RelayCommand]
        private async Task CompareQuantumBreakSnapshots()
        {
            if (IsQuantumBreakSaveDiffBusy)
                return;

            if (_snapshotA is null || _snapshotB is null)
            {
                QuantumBreakSaveDiffStatus = "Take both Snapshot A and Snapshot B before comparing.";
                return;
            }

            IsQuantumBreakSaveDiffBusy = true;
            QuantumBreakSaveDiffStatus = "Comparing snapshots...";

            try
            {
                var result = await Task.Run(() => CompareSnapshots(_snapshotA, _snapshotB));
                QuantumBreakSaveDiffRows.Clear();
                foreach (var row in result.Rows)
                    QuantumBreakSaveDiffRows.Add(row);

                QuantumBreakSaveDiffReport = result.Report;
                QuantumBreakSaveDiffStatus = result.Rows.Count == 0
                    ? "Comparison complete: no file changes detected."
                    : $"Comparison complete: {result.Rows.Count} changed files detected.";
            }
            catch (Exception ex)
            {
                QuantumBreakSaveDiffStatus = $"Comparison failed: {ex.Message}";
            }
            finally
            {
                IsQuantumBreakSaveDiffBusy = false;
            }
        }

        [RelayCommand]
        private void CopyQuantumBreakSaveDiffReport()
        {
            if (string.IsNullOrWhiteSpace(QuantumBreakSaveDiffReport))
            {
                QuantumBreakSaveDiffStatus = "Compare snapshots before copying the report.";
                return;
            }

            Clipboard.SetText(QuantumBreakSaveDiffReport);
            QuantumBreakSaveDiffStatus = "Save diff report copied to clipboard.";
        }

        [RelayCommand]
        private async Task ExportQuantumBreakSaveDiffReport()
        {
            if (string.IsNullOrWhiteSpace(QuantumBreakSaveDiffReport))
            {
                QuantumBreakSaveDiffStatus = "Compare snapshots before exporting the report.";
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"QuantumBreak-save-diff-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExt = ".txt",
                Filter = "Text report (*.txt)|*.txt|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            await File.WriteAllTextAsync(dialog.FileName, QuantumBreakSaveDiffReport);
            QuantumBreakSaveDiffStatus = $"Save diff report exported to {dialog.FileName}";
        }

        private static string DetectQuantumBreakWgsFolder()
        {
            var wgsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                QuantumBreakPackageName,
                "SystemAppData",
                "wgs");

            if (!Directory.Exists(wgsRoot))
                return wgsRoot;

            return Directory.EnumerateDirectories(wgsRoot)
                       .FirstOrDefault(path => Path.GetFileName(path).EndsWith(QuantumBreakScidFolderSuffix, StringComparison.OrdinalIgnoreCase))
                   ?? wgsRoot;
        }

        private static SaveSnapshot CaptureSaveSnapshot(string root)
        {
            var files = new Dictionary<string, SaveFileSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order())
            {
                var content = File.ReadAllBytes(path);
                var info = new FileInfo(path);
                var relativePath = Path.GetRelativePath(root, path);
                files[relativePath] = new SaveFileSnapshot(
                    relativePath,
                    content.LongLength,
                    info.LastWriteTimeUtc,
                    Convert.ToHexString(SHA256.HashData(content)),
                    content);
            }

            return new SaveSnapshot(DateTime.UtcNow, root, files);
        }

        private static SaveComparison CompareSnapshots(SaveSnapshot snapshotA, SaveSnapshot snapshotB)
        {
            var rows = new List<SaveDiffRow>();
            var allPaths = snapshotA.Files.Keys
                .Union(snapshotB.Files.Keys, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase);

            foreach (var path in allPaths)
            {
                snapshotA.Files.TryGetValue(path, out var fileA);
                snapshotB.Files.TryGetValue(path, out var fileB);

                if (fileA?.Sha256 == fileB?.Sha256)
                    continue;

                var changeType = fileA is null ? "Added" : fileB is null ? "Removed" : "Modified";
                var byteChanges = DescribeByteChanges(fileA?.Content, fileB?.Content);
                rows.Add(new SaveDiffRow
                {
                    ChangeType = changeType,
                    RelativePath = path,
                    SizeA = fileA?.Size,
                    SizeB = fileB?.Size,
                    HashA = fileA?.Sha256 ?? "",
                    HashB = fileB?.Sha256 ?? "",
                    TimestampA = fileA?.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'") ?? "",
                    TimestampB = fileB?.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'") ?? "",
                    ChangedByteRanges = byteChanges.Ranges,
                    TotalBytesChanged = byteChanges.TotalBytesChanged
                });
            }

            return new SaveComparison(rows, BuildSaveDiffReport(snapshotA, snapshotB, rows));
        }

        private static ByteChangeSummary DescribeByteChanges(byte[]? contentA, byte[]? contentB)
        {
            if (contentA is null || contentB is null)
            {
                var length = (contentA ?? contentB)?.Length ?? 0;
                return new ByteChangeSummary(
                    length == 0 ? "<empty file>" : $"0x0-0x{length - 1:X} ({length:N0} bytes)",
                    length);
            }

            var ranges = new List<string>();
            var maxLength = Math.Max(contentA.Length, contentB.Length);
            var rangeStart = -1;
            var truncated = false;
            long totalBytesChanged = 0;

            for (var offset = 0; offset < maxLength; offset++)
            {
                var different = offset >= contentA.Length
                                || offset >= contentB.Length
                                || contentA[offset] != contentB[offset];

                if (different)
                {
                    totalBytesChanged++;
                    if (rangeStart < 0)
                        rangeStart = offset;
                }

                if (!different && rangeStart >= 0)
                {
                    AddRange(rangeStart, offset - 1);
                    rangeStart = -1;
                }
            }

            if (rangeStart >= 0)
                AddRange(rangeStart, maxLength - 1);

            return new ByteChangeSummary(
                string.Join("; ", ranges) + (truncated ? $"; first {MaxDisplayedByteRangesPerFile:N0} ranges shown" : ""),
                totalBytesChanged);

            void AddRange(int start, int end)
            {
                if (ranges.Count >= MaxDisplayedByteRangesPerFile)
                {
                    truncated = true;
                    return;
                }

                ranges.Add($"0x{start:X}-0x{end:X} ({end - start + 1:N0} bytes)");
            }
        }

        private static string BuildSaveDiffReport(SaveSnapshot snapshotA, SaveSnapshot snapshotB, IReadOnlyList<SaveDiffRow> rows)
        {
            var report = new StringBuilder()
                .AppendLine("Quantum Break Save Diff Helper (read-only research)")
                .AppendLine($"WGS folder: {QuantumBreakWgsFolderForReport(snapshotA, snapshotB)}")
                .AppendLine($"Snapshot A: {snapshotA.CapturedAtUtc:O} ({snapshotA.Files.Count} files)")
                .AppendLine($"Snapshot B: {snapshotB.CapturedAtUtc:O} ({snapshotB.Files.Count} files)")
                .AppendLine($"Changed files: {rows.Count}")
                .AppendLine();

            foreach (var row in rows)
            {
                report.AppendLine($"[{row.ChangeType}] {row.RelativePath}")
                    .AppendLine($"  Size A: {FormatSize(row.SizeA)}")
                    .AppendLine($"  Size B: {FormatSize(row.SizeB)}")
                    .AppendLine($"  SHA-256 A: {FormatValue(row.HashA)}")
                    .AppendLine($"  SHA-256 B: {FormatValue(row.HashB)}")
                    .AppendLine($"  Timestamp A: {FormatValue(row.TimestampA)}")
                    .AppendLine($"  Timestamp B: {FormatValue(row.TimestampB)}")
                    .AppendLine($"  Total bytes changed: {row.TotalBytesChanged:N0}")
                    .AppendLine($"  Changed byte ranges: {row.ChangedByteRanges}")
                    .AppendLine();
            }

            return report.ToString();

            static string FormatSize(long? value) => value.HasValue ? $"{value.Value:N0} bytes" : "<not present>";
            static string FormatValue(string value) => string.IsNullOrWhiteSpace(value) ? "<not present>" : value;
        }

        private static string QuantumBreakWgsFolderForReport(SaveSnapshot snapshotA, SaveSnapshot snapshotB)
        {
            return snapshotA.RootPath == snapshotB.RootPath
                ? snapshotA.RootPath
                : $"{snapshotA.RootPath} -> {snapshotB.RootPath}";
        }

        [RelayCommand]
        public void TestEventReplacements()
        {
            int failedAchievements = 0;
            int successfulAchievements = 0;
            List<string> errors = new List<string>();
            string DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "Events", "Data.json");
            var data = JObject.Parse(File.ReadAllText(DataPath));
            JArray SupportedGamesJ = (JArray)data["SupportedTitleIDs"];
            string Achievement = "";
            DateTime timestamp = DateTime.UtcNow;
            foreach (var game in SupportedGamesJ)
            {
                var requestbody = File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "Events", $"{game}.json"));

                var EventsData = (dynamic)(JObject)data[game.ToString()];
                foreach (var i in EventsData.Achievements)
                {
                    try
                    {
                        foreach (var j in i)
                        {
                            Achievement = i.Name.ToString();
                            foreach (var k in j)
                            {
                                var ReplacementData = k.Value;
                                switch (ReplacementData.ReplacementType.ToString())
                                {
                                    case "Replace":
                                        {
                                            requestbody = requestbody.Replace(ReplacementData.Target.ToString(), ReplacementData.Replacement.ToString());
                                            break;
                                        }
                                    case "RangeInt":
                                        {
                                            int min = ReplacementData.Min;
                                            int max = ReplacementData.Max;
                                            Random random = new Random();
                                            int randomint = random.Next(min, max);
                                            requestbody = requestbody.Replace(ReplacementData.Target.ToString(), randomint.ToString());
                                            break;
                                        }
                                    case "RangeFloat":
                                        {
                                            float min = ReplacementData.Min;
                                            float max = ReplacementData.Max;
                                            Random random = new Random();
                                            float randomfloat = (float)random.NextDouble() * (max - min) + min;
                                            requestbody = requestbody.Replace(ReplacementData.Target.ToString(), randomfloat.ToString());
                                            break;
                                        }
                                    case "StupidFuckingLDAPTimestamp":
                                        {
                                            long ldapTimestamp = DateTime.Now.ToFileTime();
                                            requestbody = requestbody.Replace(ReplacementData.Target.ToString(), ldapTimestamp.ToString());
                                            break;
                                        }
                                    default:
                                        {
                                            //_snackbarService.Show("Error: Bad Achievement Data", "Something went wrong with the achievement data", ControlAppearance.Danger,
                                            //                                                  new SymbolIcon(SymbolRegular.ErrorCircle24), _snackbarDuration);
                                            return;
                                        }

                                }
                            }
                        }
                        requestbody = requestbody.Replace("REPLACETIME", timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
                        requestbody = requestbody.Replace("REPLACESEQ", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                        requestbody = requestbody.Replace("REPLACEXUID", HomeViewModel.XUIDOnly);
                        requestbody = JObject.Parse(requestbody).ToString(Formatting.None);
                        var bodyconverted = new StringContent(requestbody, Encoding.UTF8, "application/x-json-stream");
                        successfulAchievements++;
                    }
                    catch (Exception ex)
                    {
                        failedAchievements++;
                        errors.Add($"Game: {game}, Achievement:{Achievement}, Error: {ex.Message}");
                    }
                }

            }

            // Write errors to a file
            File.WriteAllLines(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XAU", "Events", "Errors.log"), errors);

            // Show message box with the summary
            _contentDialogService.ShowSimpleDialogAsync(
                        new SimpleContentDialogCreateOptions()
                        {
                            Title = "Test Results",
                            Content = $"Successful Achievements: {successfulAchievements}\nUnsuccessful Achievements: {failedAchievements}",
                            CloseButtonText = "Close"
                        });
        }

        private sealed record SaveSnapshot(
            DateTime CapturedAtUtc,
            string RootPath,
            Dictionary<string, SaveFileSnapshot> Files);

        private sealed record SaveFileSnapshot(
            string RelativePath,
            long Size,
            DateTime LastWriteTimeUtc,
            string Sha256,
            byte[] Content);

        private sealed record SaveComparison(List<SaveDiffRow> Rows, string Report);

        private sealed record ByteChangeSummary(string Ranges, long TotalBytesChanged);

        private sealed record WatchSignal(
            string Signal,
            bool IsImportant,
            bool IsMetadataOnly,
            bool IsSavegamesPayloadChange);

        private sealed record SavegamesVersion(string ContainerFileName, string Hash, SaveSnapshot Snapshot)
        {
            public string HashPrefix => Hash[..Math.Min(12, Hash.Length)];
        }

        public sealed class SaveDiffRow
        {
            public string ChangeType { get; init; } = "";
            public string RelativePath { get; init; } = "";
            public long? SizeA { get; init; }
            public long? SizeB { get; init; }
            public string HashA { get; init; } = "";
            public string HashB { get; init; } = "";
            public string HashAPrefix => string.IsNullOrEmpty(HashA) ? "" : HashA[..Math.Min(12, HashA.Length)];
            public string HashBPrefix => string.IsNullOrEmpty(HashB) ? "" : HashB[..Math.Min(12, HashB.Length)];
            public string TimestampA { get; init; } = "";
            public string TimestampB { get; init; } = "";
            public string ChangedByteRanges { get; init; } = "";
            public long TotalBytesChanged { get; init; }
        }

        public sealed class SaveWatchRow
        {
            public string ChangeType { get; init; } = "";
            public string RelativePath { get; init; } = "";
            public string Container { get; init; } = "";
            public string Timestamp { get; init; } = "";
            public long? Size { get; init; }
            public string Hash { get; init; } = "";
            public string Signal { get; init; } = "";
            public bool IsImportant { get; init; }
            public bool IsMetadataOnly { get; init; }
            public bool IsSavegamesPayloadChange { get; init; }
            public string SizeDisplay => Size.HasValue ? $"{Size.Value:N0}" : "<not present>";
            public string HashDisplay => string.IsNullOrEmpty(Hash) ? "<not available>" : Hash;
            public string HashPrefix => string.IsNullOrEmpty(Hash) ? "" : Hash[..Math.Min(12, Hash.Length)];
        }

    }
}
