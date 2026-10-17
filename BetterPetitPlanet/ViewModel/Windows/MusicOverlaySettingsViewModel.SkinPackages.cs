using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterPetitPlanet.ViewModel.Windows;

public partial class MusicOverlaySettingsViewModel
{
    private readonly MusicSkinRepository _skinRepository;
    private readonly MusicSkinPackageImporter _skinPackageImporter;
    private readonly Func<string, CancellationToken, Task<MusicSkinLoadResult>> _loadSkin;
    private CancellationTokenSource? _skinLoadCancellation;
    private CancellationTokenSource? _importCancellation;
    private long _skinLoadRevision;

    [ObservableProperty] private string _selectedSkinId = "default";
    [ObservableProperty] private MusicSkinInfo? _selectedSkinInfo;
    [ObservableProperty] private MusicSkin _activeSkin = MusicSkin.Default;
    [ObservableProperty] private string? _skinDiagnostic;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importStatusMessage = "仅支持本地 .zip 数据皮肤包";
    [ObservableProperty] private string _skinSearchText = "";

    public ObservableCollection<MusicSkinInfo> AvailableSkins { get; } = new();
    public ObservableCollection<MusicSkinInfo> FilteredSkins { get; } = new();
    public event Action<MusicSkin>? SkinChanged;
    public string? OverlaySkinDiagnostic => _presenter?.SkinDiagnostic;
    public string? OverlayBackendDiagnostic => _presenter?.BackendDiagnostic;

    public static bool IsSkinIdMatch(string? id1, string? id2)
    {
        if (string.Equals(id1, id2, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(id1) || string.IsNullOrWhiteSpace(id2)) return false;

        static string Normalize(string id)
        {
            string s = id.Trim();
            if (s.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("builtin:".Length);
            if (string.Equals(s, "default", StringComparison.OrdinalIgnoreCase))
                return "nahida";
            return s;
        }

        return string.Equals(Normalize(id1), Normalize(id2), StringComparison.OrdinalIgnoreCase);
    }

    public MusicSkinInfo? FindSkinInfo(string? skinId)
    {
        if (string.IsNullOrWhiteSpace(skinId)) return null;
        var exact = AvailableSkins.FirstOrDefault(s => string.Equals(s.Id, skinId, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        return AvailableSkins.FirstOrDefault(s => IsSkinIdMatch(s.Id, skinId));
    }

    partial void OnSkinSearchTextChanged(string value) => ApplySkinFilter();

    private void ApplySkinFilter()
    {
        var query = SkinSearchText?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(query)
            ? AvailableSkins
            : AvailableSkins.Where(skin =>
                (skin.CharacterName != null && skin.CharacterName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (skin.DisplayName != null && skin.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (skin.FullTitle != null && skin.FullTitle.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (skin.Region != null && skin.Region.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (skin.Id != null && (skin.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     IsSkinIdMatch(skin.Id, query))));

        FilteredSkins.Clear();
        foreach (var item in filtered)
        {
            FilteredSkins.Add(item);
        }
    }

    private void ReloadAvailableSkins()
    {
        AvailableSkins.Clear();
        foreach (var skin in _skinRepository.GetAvailableSkins())
        {
            AvailableSkins.Add(skin);
        }
        ApplySkinFilter();
    }

    /// <summary>Imports a local skin ZIP into the repository and selects it as an unapplied settings draft.</summary>
    public async Task ImportSkinPackageAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (_disposed || IsImporting) return;
        IsImporting = true;
        ImportStatusMessage = "正在验证并导入皮肤包…";
        using var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _importCancellation = importCancellation;
        try
        {
            MusicSkinPackageImportResult result = await _skinPackageImporter.ImportAsync(archivePath, importCancellation.Token);
            if (_disposed) return;

            ReloadAvailableSkins();

            MusicSkinInfo? importedSkin = FindSkinInfo(result.Id);
            if (importedSkin == null)
                throw new InvalidOperationException("皮肤已安装，但刷新列表时未找到该皮肤");

            SelectedSkinId = result.Id;
            SelectedSkinInfo = importedSkin;
            await LoadSelectedSkinAsync(result.Id);
            if (_disposed) return;

            IsDirty = ComputeIsDirty();
            ImportStatusMessage = IsDirty
                ? $"已导入并预览“{result.DisplayName}”；点击“应用谱面设置”后才会保存选择"
                : $"已导入并预览“{result.DisplayName}”；配置选择保持不变";
            if (result.CleanupWarning != null)
                ImportStatusMessage += "；" + result.CleanupWarning;
            StatusMessage = IsDirty
                ? "皮肤已加入当前草稿，点击“应用谱面设置”保存生效"
                : "已导入皮肤，当前配置与草稿同步";
        }
        catch (OperationCanceledException) when (importCancellation.IsCancellationRequested)
        {
            if (!_disposed) ImportStatusMessage = "皮肤导入已取消";
        }
        catch (Exception ex)
        {
            if (!_disposed) ImportStatusMessage = "皮肤导入失败：" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_importCancellation, importCancellation)) _importCancellation = null;
            if (!_disposed) IsImporting = false;
        }
    }

    private void OnPresenterChanged()
    {
        if (!_disposed)
        {
            OnPropertyChanged(nameof(OverlaySkinDiagnostic));
            OnPropertyChanged(nameof(OverlayBackendDiagnostic));
        }
    }

    partial void OnSelectedSkinInfoChanged(MusicSkinInfo? value)
    {
        if (value != null && !IsSkinIdMatch(SelectedSkinId, value.Id))
        {
            SelectedSkinId = value.Id;
        }
    }

    partial void OnSelectedSkinIdChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewBackendStatus));
        var match = FindSkinInfo(value);
        if (!Equals(SelectedSkinInfo, match))
        {
            SelectedSkinInfo = match;
        }
        _ = LoadSelectedSkinAsync(value);
        OnDraftChanged();
    }

    public async Task LoadSelectedSkinAsync(string skinId)
    {
        if (_disposed || _previewSuspended) return;
        long revision = ++_skinLoadRevision;
        _skinLoadCancellation?.Cancel();
        _skinLoadCancellation?.Dispose();
        var cancellation = _skinLoadCancellation = new CancellationTokenSource();
        try
        {
            var result = await _loadSkin(skinId, cancellation.Token);
            if (_disposed || _previewSuspended || revision != _skinLoadRevision) return;
            SkinDiagnostic = result.Diagnostic;
            ActiveSkin = result.Skin;
            SkinChanged?.Invoke(result.Skin);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && !_previewSuspended && revision == _skinLoadRevision)
            {
                SkinDiagnostic = "加载皮肤失败：" + ex.Message;
                ActiveSkin = MusicSkin.Default;
                SkinChanged?.Invoke(ActiveSkin);
            }
        }
    }

    internal void SuspendPreviewResources()
    {
        if (_disposed) return;
        _previewSuspended = true;
        _skinLoadRevision++;
        _skinLoadCancellation?.Cancel();
        PausePreviewCore();
        ActiveSkin = MusicSkin.Default;
    }

    internal Task ResumePreviewResourcesAsync()
    {
        if (_disposed || !_previewSuspended) return Task.CompletedTask;
        _previewSuspended = false;
        return LoadSelectedSkinAsync(SelectedSkinId);
    }

    private void DisposeSkinResources()
    {
        _importCancellation?.Cancel();
        _skinLoadRevision++;
        _skinLoadCancellation?.Cancel();
        _skinLoadCancellation?.Dispose();
        _skinLoadCancellation = null;
        if (_presenter != null) _presenter.Changed -= OnPresenterChanged;
        ActiveSkin = MusicSkin.Default;
    }
}
