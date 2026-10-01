using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BetterPetitPlanet.Core.Config;

public sealed class ConfigService : IConfigService
{
    private readonly ILogger<ConfigService>? _logger;
    private readonly string _configFilePath;
    private AppConfig _config;

    public AppConfig Config => _config;

    public ConfigService(ILogger<ConfigService>? logger = null)
    {
        _logger = logger;
        _configFilePath = Path.Combine(AppContext.BaseDirectory, "config", "config.json");
        _config = LoadInternal();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
            File.WriteAllText(_configFilePath, json);
            _logger?.LogInformation("Configuration successfully saved to {Path}", _configFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save configuration to {Path}", _configFilePath);
        }
    }

    public void Reload()
    {
        _config = LoadInternal();
        _logger?.LogInformation("Configuration reloaded from {Path}", _configFilePath);
    }

    private AppConfig LoadInternal()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var config = JsonConvert.DeserializeObject<AppConfig>(json);
                if (config != null)
                {
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read configuration from {Path}, fallback to defaults", _configFilePath);
        }

        var defaultConfig = new AppConfig();
        return defaultConfig;
    }
}
