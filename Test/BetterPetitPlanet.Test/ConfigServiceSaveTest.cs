using System;
using System.IO;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class ConfigServiceSaveTest
{
    private sealed class ThrowingDebugLogger : ILogger<ConfigService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug)
            {
                throw new InvalidOperationException("test success-log failure");
            }
        }
    }

    [Fact]
    public async Task SaveAsync_SuccessLogFailureDoesNotMaskCommittedConfig()
    {
        var relativePath = "hotkey-config-save-" + Guid.NewGuid().ToString("N") + ".json";
        var configPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        var temporaryPath = configPath + ".tmp";

        try
        {
            var configService = new ConfigService(new ThrowingDebugLogger(), relativePath);
            configService.Config.Hotkey.ToggleAutoPickHotkey = "F7";

            await configService.SaveAsync();

            Assert.True(File.Exists(configPath));
            var saved = JsonConvert.DeserializeObject<AppConfig>(await File.ReadAllTextAsync(configPath));
            Assert.NotNull(saved);
            Assert.Equal("F7", saved.Hotkey.ToggleAutoPickHotkey);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
