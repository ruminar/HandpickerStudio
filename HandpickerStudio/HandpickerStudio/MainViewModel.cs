using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HandpickerStudio;

internal sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsStore store;
    private WorkspaceSettings settings = WorkspaceSettings.Empty;
    private CancellationTokenSource? currentLoad;
    private Project? selectedProject;
    private Checkpoint? selectedCheckpoint;
    private ImageEntry[] allImages = [];
    private bool hasScannedImages;
    private HeldLabel[] observedHolds = [];
    private bool showingUnresolved;
    private bool updatingProjects;
    private string settingsNotice = "";
    private string cacheNotice = "";
    private string[] scanWarnings = [];

    public MainViewModel(SettingsStore store)
    {
        this.store = store;
        CanSave = store.CanWrite;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Project[] Projects => settings.Projects;
    public Checkpoint? SelectedCheckpoint => selectedCheckpoint;
    public HeldLabel[] HeldLabels => observedHolds.Concat(settings.HeldLabels).DistinctBy(CatalogService.HoldKey).ToArray();
    public bool CanSave { get; private set; }
    internal bool SettingsLoaded { get; private set; }
    internal bool CheckUpdatesOnStartup => settings.CheckUpdatesOnStartup;
    internal string[] CacheLocations => settings.PreviewCacheRoots ?? [];
    public bool CanEditProject => CanSave && SelectedProject is not null;
    public AppearanceTheme Appearance => Enum.IsDefined(settings.Appearance) ? settings.Appearance : AppearanceTheme.Light;
    public bool CreateImageCache => settings.CreateImageCache;
    public PreviewCache? GalleryCache { get; private set; }
    public GalleryLayout GalleryView { get; private set; }
    public int GalleryViewIndex => (int)GalleryView;
    public int TileSize { get; private set; } = WorkspaceSettings.DefaultTileSize;
    public bool IsLoading { get; private set; }
    public bool CanRegisterMissingThumbnails => !IsLoading && hasScannedImages && Groups.Any(group => group.Items.Length > 0);
    public CheckpointGroup[] Groups { get; private set; } = [];
    public RootStatus[] Roots { get; private set; } = [];
    public ImageEntry[] Images { get; private set; } = [];
    public string CollisionReport { get; private set; } = "";
    public bool HasCollisions => CollisionReport.Length > 0;
    public int UnresolvedCount => allImages.Count(image => image.Problem is not null);
    public string UnresolvedLabel => $"所属未確定 ({UnresolvedCount})";
    public string GalleryTitle => showingUnresolved ? "所属未確定" : selectedCheckpoint?.Name ?? "画像一覧";
    public string GalleryContext => selectedCheckpoint?.RelativePath ?? "";
    public string StatusText => IsLoading ? "走査中…" : $"{Images.Length} 枚";
    public string Warnings => string.Join(Environment.NewLine,
        new[] { settingsNotice, cacheNotice }.Concat(scanWarnings).Where(value => value.Length != 0));
    public bool HasWarnings => Warnings.Length > 0;
    public bool IsGalleryEmpty => Images.Length == 0;
    public string EmptyGalleryMessage => SelectedProject is null ? "プロジェクトを登録してください。" : IsLoading ? "走査中…" :
        selectedCheckpoint is null && !showingUnresolved ? "左の一覧からCheckpointを選択してください。" : "表示できる画像がありません。";

    public Project? SelectedProject
    {
        get => selectedProject;
        set
        {
            if (updatingProjects || ReferenceEquals(value, selectedProject))
                return;

            selectedProject = value;
            selectedCheckpoint = null;
            showingUnresolved = false;
            if (CanSave && settings.SelectedProjectId != value?.Id)
                TrySave(settings with { SelectedProjectId = value?.Id });

            UpdateGalleryCache();
            Changed();
            Changed(nameof(CanEditProject));
            _ = ReloadAsync();
        }
    }

    internal MainWindowPlacement? LoadSettings()
    {
        try
        {
            settings = store.Load();
            SettingsLoaded = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        {
            CanSave = false;
            settingsNotice = $"設定を読み込めません。元の設定を保持します。{exception.Message}";
            Changed(nameof(CanSave));
        }

        return settings.MainWindowPlacement;
    }

    internal bool SavePlacement(MainWindowPlacement placement) =>
        settings.MainWindowPlacement == placement || TrySave(settings with { MainWindowPlacement = placement });

    public async Task InitializeAsync()
    {
        GalleryView = settings.Layout is { } layout && Enum.IsDefined(layout) ? layout :
            settings.UseTileView ? GalleryLayout.Tiles : GalleryLayout.List;
        TileSize = Math.Clamp(settings.TileSize, WorkspaceSettings.MinimumTileSize, WorkspaceSettings.MaximumTileSize);
        if (settings.CreateImageCache && settings.PreviewCacheOwner.GetValueOrDefault() == Guid.Empty)
            TrySave(settings);
        Changed(nameof(Appearance));
        Changed(nameof(GalleryView));
        Changed(nameof(GalleryViewIndex));
        Changed(nameof(TileSize));
        UpdateProjects(settings.Projects.FirstOrDefault(project => project.Id == settings.SelectedProjectId) ??
            settings.Projects.FirstOrDefault());
        await Task.WhenAll(ReloadAsync(), ClearDisabledCacheAsync());
    }

    public async Task<bool> SetProjectAsync(Project project, bool? autoArchive = null)
    {
        Project[] projects = settings.Projects.Any(item => item.Id == project.Id)
            ? settings.Projects.Select(item => item.Id == project.Id ? project : item).ToArray()
            : [.. settings.Projects, project];
        var next = settings with { Projects = projects, SelectedProjectId = project.Id };
        if (autoArchive is { } automatic)
            next = WithArchivePreference(next, GetArchivePreference(project.Id) with { Automatic = automatic });
        if (!TrySave(next))
            return false;

        if (selectedProject?.Id != project.Id)
        {
            selectedCheckpoint = null;
            showingUnresolved = false;
        }

        UpdateProjects(project);
        await ReloadAsync();
        return true;
    }

    public async Task RemoveProjectAsync(Project project)
    {
        Project[] projects = settings.Projects.Where(item => item.Id != project.Id).ToArray();
        Project? next = projects.FirstOrDefault();
        if (!TrySave(settings with { Projects = projects, SelectedProjectId = next?.Id,
            ArchivePreferences = settings.ArchivePreferences?.Where(item => item.ProjectId != project.Id).ToArray() }))
            return;

        selectedCheckpoint = null;
        showingUnresolved = false;
        UpdateProjects(next);
        await ReloadAsync();
    }

    public Task ToggleRootAsync(ImageRoot root)
    {
        if (selectedProject is not { } project)
            return Task.CompletedTask;

        return SetProjectAsync(project with
        {
            Roots = project.Roots.Select(item => item.Id == root.Id ? item with { Enabled = !item.Enabled } : item).ToArray()
        });
    }

    public void SelectCheckpoint(Checkpoint checkpoint)
    {
        if (checkpoint.Project.Id != selectedProject?.Id)
            return;

        selectedCheckpoint = checkpoint;
        showingUnresolved = false;
        UpdateGallery();
    }

    public void ShowUnresolved()
    {
        selectedCheckpoint = null;
        showingUnresolved = true;
        UpdateGallery();
    }

    internal async Task ReleaseLegacyHoldsAsync(HeldLabel[] holds)
    {
        if (IsLoading || !CanSave)
            return;

        var requested = holds.Select(CatalogService.HoldKey).ToHashSet();
        var remaining = settings.HeldLabels.Where(hold => hold.Scoped || !requested.Contains(CatalogService.HoldKey(hold))).ToArray();
        if (remaining.Length != settings.HeldLabels.Length && TrySave(settings with { HeldLabels = remaining }))
            await ReloadAsync();
    }

    public void ClearCheckpointSelection()
    {
        selectedCheckpoint = null;
        showingUnresolved = false;
        UpdateGallery();
    }

    public void ChangeGalleryView(GalleryLayout layout)
    {
        if (GalleryView == layout)
            return;

        GalleryView = layout;
        Changed(nameof(GalleryView));
        Changed(nameof(GalleryViewIndex));
        if (CanSave)
            TrySave(settings with { Layout = layout, UseTileView = layout != GalleryLayout.List });
    }

    public void ChangeTileSize(int size)
    {
        size = Math.Clamp(size, WorkspaceSettings.MinimumTileSize, WorkspaceSettings.MaximumTileSize);
        if (TileSize == size)
            return;

        TileSize = size;
        Changed(nameof(TileSize));
        if (CanSave)
            TrySave(settings with { TileSize = size });
    }

    public async Task ChangeDisplaySettingsAsync(AppearanceTheme appearance, bool createCache, bool checkUpdatesOnStartup)
    {
        bool cacheChanged = CreateImageCache != createCache;
        if ((Appearance != appearance || cacheChanged || CheckUpdatesOnStartup != checkUpdatesOnStartup) &&
            TrySave(settings with { Appearance = appearance, CreateImageCache = createCache, CheckUpdatesOnStartup = checkUpdatesOnStartup }))
        {
            Changed(nameof(Appearance));
            if (cacheChanged)
                UpdateGalleryCache();
        }

        if (!CreateImageCache)
            await ClearDisabledCacheAsync();
        else if (cacheNotice.Length > 0)
        {
            cacheNotice = "";
            NotifyWarnings();
        }
    }

    internal Task<string> RegisterMissingThumbnailsAsync(Project project)
    {
        var checkpoints = Groups.SelectMany(group => group.Items).ToArray();
        var images = allImages;
        return Task.Run(() => ImageOperations.RegisterMissingThumbnails(project, checkpoints, images));
    }

    internal ArchivePreference GetArchivePreference(Guid projectId) =>
        settings.ArchivePreferences?.FirstOrDefault(item => item is not null && item.ProjectId == projectId) ?? new(projectId);

    internal bool SaveArchivePreference(ArchivePreference preference) =>
        GetArchivePreference(preference.ProjectId) == preference || TrySave(WithArchivePreference(settings, preference));

    private static WorkspaceSettings WithArchivePreference(WorkspaceSettings existing, ArchivePreference preference) =>
        existing with
        {
            ArchivePreferences = [.. (existing.ArchivePreferences ?? []).Where(item => item is not null &&
                item.ProjectId != preference.ProjectId), preference]
        };

    public async Task ReloadAsync(bool keepGallery = false, Action? beforeGalleryUpdate = null)
    {
        currentLoad?.Cancel();
        using var load = new CancellationTokenSource();
        currentLoad = load;
        var token = load.Token;
        Project? project = selectedProject;
        Project[] projects = settings.Projects;
        string? selectedPath = selectedCheckpoint?.RelativePath;

        Groups = [];
        hasScannedImages = false;
        if (!keepGallery)
            allImages = [];
        Roots = project?.Roots.Select(root => new RootStatus(root, root.Enabled ? "確認中…" : "無効・接続未確認")).ToArray() ?? [];
        CollisionReport = "";
        scanWarnings = [];
        IsLoading = project is not null;
        NotifyScan();
        if (!keepGallery)
            UpdateGallery();

        try
        {
            if (project is null)
                return;

            var catalog = await Task.Run(() => CatalogService.ReadCatalog(projects, project, token), token);
            token.ThrowIfCancellationRequested();
            Groups = catalog.Groups;
            selectedCheckpoint = catalog.Checkpoints.FirstOrDefault(item => item.RelativePath == selectedPath);
            CollisionReport = catalog.CollisionReport;
            scanWarnings = catalog.Warnings;

            observedHolds = observedHolds.Concat(catalog.ObservedHolds).DistinctBy(CatalogService.HoldKey).ToArray();
            var heldLabels = HeldLabels;
            if (!heldLabels.SequenceEqual(settings.HeldLabels))
                TrySave(settings with { HeldLabels = heldLabels });

            NotifyScan();
            if (!keepGallery)
                UpdateGallery();

            var scan = await Task.Run(() => CatalogService.ReadImages(project, catalog, heldLabels, token), token);
            token.ThrowIfCancellationRequested();
            allImages = scan.Images;
            hasScannedImages = true;
            Roots = scan.Roots;
            scanWarnings = catalog.Warnings.Concat(scan.Warnings).ToArray();
            NotifyScan();
            beforeGalleryUpdate?.Invoke();
            UpdateGallery();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (ReferenceEquals(currentLoad, load))
            {
                scanWarnings = [.. scanWarnings, $"走査を完了できません。{exception.Message}"];
                NotifyScan();
            }
        }
        finally
        {
            if (ReferenceEquals(currentLoad, load))
            {
                currentLoad = null;
                IsLoading = false;
                Changed(nameof(IsLoading));
                Changed(nameof(CanRegisterMissingThumbnails));
                Changed(nameof(StatusText));
                Changed(nameof(EmptyGalleryMessage));
            }
        }
    }

    public void Cancel()
    {
        currentLoad?.Cancel();
        GalleryCache?.Stop();
    }

    private void UpdateGalleryCache()
    {
        GalleryCache?.Stop();
        GalleryCache = CreateImageCache && selectedProject is { } project &&
            settings.PreviewCacheOwner is { } owner && owner != Guid.Empty
            ? new PreviewCache(project, owner, store) : null;
        Changed(nameof(GalleryCache));
    }

    private async Task ClearDisabledCacheAsync()
    {
        if (CreateImageCache || settings.PreviewCacheOwner is not { } owner || owner == Guid.Empty ||
            settings.PreviewCacheRoots is not { Length: > 0 } roots)
            return;

        var failures = await Task.Run(() =>
        {
            var messages = new List<string>();
            foreach (var root in roots)
            {
                try { PreviewCache.Clear(root, owner, store); }
                catch (Exception error) when (PreviewCache.IsCacheError(error)) { messages.Add($"{root} {error.Message}"); }
            }
            return messages;
        });

        if (!CreateImageCache)
        {
            cacheNotice = failures.Count == 0 ? "" :
                $"画像キャッシュの削除を完了できません。{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";
            NotifyWarnings();
        }
    }

    private bool TrySave(WorkspaceSettings next)
    {
        if (!CanSave)
            return false;

        try
        {
            if (next.CreateImageCache)
            {
                var owner = next.PreviewCacheOwner.GetValueOrDefault();
                next = next with
                {
                    PreviewCacheOwner = owner == Guid.Empty ? Guid.NewGuid() : owner,
                    PreviewCacheRoots = (next.PreviewCacheRoots ?? []).Concat(next.Projects.Select(WorkspacePaths.CacheBase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                };
            }
            store.Save(next);
            settings = next;
            settingsNotice = "";
            NotifyWarnings();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            settingsNotice = $"設定を保存できません。{exception.Message}";
            NotifyWarnings();
            return false;
        }
    }

    private void UpdateProjects(Project? project)
    {
        updatingProjects = true;
        try
        {
            selectedProject = project;
            UpdateGalleryCache();
            Changed(nameof(Projects));
            Changed(nameof(SelectedProject));
            Changed(nameof(CanEditProject));
        }
        finally { updatingProjects = false; }
    }

    private void UpdateGallery()
    {
        Images = showingUnresolved ? allImages.Where(item => item.Problem is not null).ToArray() :
            selectedCheckpoint is { } checkpoint ? allImages.Where(item => item.CheckpointPath == checkpoint.RelativePath).ToArray() : [];
        Changed(nameof(Images));
        Changed(nameof(SelectedCheckpoint));
        Changed(nameof(GalleryTitle));
        Changed(nameof(GalleryContext));
        Changed(nameof(IsGalleryEmpty));
        Changed(nameof(EmptyGalleryMessage));
        Changed(nameof(StatusText));
    }

    private void NotifyWarnings()
    {
        Changed(nameof(Warnings));
        Changed(nameof(HasWarnings));
    }

    private void NotifyScan()
    {
        Changed(nameof(Groups));
        Changed(nameof(Roots));
        Changed(nameof(IsLoading));
        Changed(nameof(CanRegisterMissingThumbnails));
        Changed(nameof(HasCollisions));
        Changed(nameof(UnresolvedLabel));
        NotifyWarnings();
    }

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
