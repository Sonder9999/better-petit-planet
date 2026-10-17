using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.GameTask;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterPetitPlanet.Core.Config;

public sealed class ConfigService : IConfigService
{
    private readonly ILogger<ConfigService>? _logger;
    private readonly string _configFilePath;
    private AppConfig _config;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public AppConfig Config => _config;

    public ConfigService(ILogger<ConfigService>? logger = null, string configRelativePath = "config/config.json")
    {
        _logger = logger;
        if (Path.IsPathRooted(configRelativePath) || configRelativePath.Contains(".."))
            throw new ArgumentException("配置路径必须相对于应用目录", nameof(configRelativePath));
        _configFilePath = Path.Combine(AppContext.BaseDirectory, configRelativePath);
        _config = LoadInternal();
    }

    public void Save() => _ = SaveWithoutPropagationAsync();

    private async Task SaveWithoutPropagationAsync()
    {
        try
        {
            await SaveAsync().ConfigureAwait(false);
        }
        catch
        {
            // SaveAsync already logged; legacy fire-and-forget callers have no exception channel.
        }
    }

    public async Task SaveAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
            await WriteJsonAsync(json).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task SaveMusicOverlayAsync(MusicOverlayConfig settings)
    {
        var snapshot = settings.Clone();
        snapshot.Validate();
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var candidate = _config.MusicOverlay.Clone();
            candidate.ApplySettingsFrom(snapshot);
            candidate.Validate();
            var document = JObject.FromObject(_config);
            document[nameof(AppConfig.MusicOverlay)] = JObject.FromObject(candidate);
            await WriteJsonAsync(document.ToString(Formatting.Indented)).ConfigureAwait(false);
            _config.MusicOverlay.ApplySettingsFrom(candidate);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task WriteJsonAsync(string json)
    {
        try
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _configFilePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
            if (File.Exists(_configFilePath))
                File.Replace(temporaryPath, _configFilePath, _configFilePath + ".bak");
            else
                File.Move(temporaryPath, _configFilePath);
            try
            {
                _logger?.LogDebug("Configuration saved to {Path}", _configFilePath);
            }
            catch (Exception)
            {
                // A diagnostic failure must not turn a committed save into a reported failure.
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save configuration to {Path}", _configFilePath);
            throw;
        }
    }

    public void Reload() => _ = InitializeAsync();

    public async Task InitializeAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _config = await LoadInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private AppConfig LoadInternal()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                return ParseAndValidate(json);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read configuration from {Path}, fallback to defaults", _configFilePath);
        }

        return new AppConfig();
    }

    private async Task<AppConfig> LoadInternalAsync()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = await File.ReadAllTextAsync(_configFilePath).ConfigureAwait(false);
                return ParseAndValidate(json);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read configuration from {Path}, fallback to defaults", _configFilePath);
        }

        return new AppConfig();
    }

    private AppConfig ParseAndValidate(string json)
    {
        var config = JsonConvert.DeserializeObject<AppConfig>(json);
        if (config == null)
        {
            return new AppConfig();
        }

        config.General ??= new();
        config.AutoPick ??= new AutoPickConfig();
        config.Music ??= new();
        config.Hotkey ??= new();

        if (config.Music.PreparationMs < 0 || config.Music.PreparationMs > 10000 ||
            !double.IsFinite(config.Music.PlaybackSpeed) || config.Music.PlaybackSpeed < 0.2 || config.Music.PlaybackSpeed > 5 ||
            !Enum.IsDefined(config.Music.InputMode) || !Enum.IsDefined(config.Music.PlaybackMode))
        {
            _logger?.LogWarning("Invalid music settings reset to defaults");
            config.Music = new();
        }

        config.Music.PlaybackTiming ??= new();
        try
        {
            config.Music.PlaybackTiming.Validate();
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Invalid music timing reset to defaults");
            config.Music.PlaybackTiming = new();
        }

        try
        {
            MusicHotkey.Validate(config.Hotkey);
        }
        catch (ArgumentException ex)
        {
            _logger?.LogWarning(ex, "Invalid hotkeys reset to defaults");
            config.Hotkey = new();
        }

        config.MusicOverlay = MusicOverlayConfigMigration.Migrate(config.MusicOverlay, _logger);

        if (config.AutoPick.Whitelist == null || config.AutoPick.Whitelist.Count == 0)
        {
            config.AutoPick.Whitelist = ["拾取"];
        }
        else
        {
            config.AutoPick.Whitelist = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Where(config.AutoPick.Whitelist, w => !string.IsNullOrWhiteSpace(w))));
        }

        if (config.AutoPick.Blacklist == null || config.AutoPick.Blacklist.Count == 0)
        {
            config.AutoPick.Blacklist = ["拾取雪球"];
        }
        else
        {
            config.AutoPick.Blacklist = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Where(config.AutoPick.Blacklist, b => !string.IsNullOrWhiteSpace(b))));
        }

        return config;
    }
}
