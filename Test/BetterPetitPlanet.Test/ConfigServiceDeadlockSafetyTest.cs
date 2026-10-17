using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using Newtonsoft.Json;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class ConfigServiceDeadlockSafetyTest : IDisposable
{
    private readonly string _relativeConfigFileName;
    private readonly string _absoluteConfigFilePath;

    public ConfigServiceDeadlockSafetyTest()
    {
        _relativeConfigFileName = $"test_config_{Guid.NewGuid():N}.json";
        _absoluteConfigFilePath = Path.Combine(AppContext.BaseDirectory, _relativeConfigFileName);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_absoluteConfigFilePath))
            {
                File.Delete(_absoluteConfigFilePath);
            }
            var tmpFile = _absoluteConfigFilePath + ".tmp";
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
        catch
        {
            // Ignore test cleanup
        }
    }

    [Fact]
    public void Constructor_OnSingleThreadSynchronizationContext_DoesNotDeadlock()
    {
        // Simulate a WPF Dispatcher STA synchronization context that would deadlock if any sync-over-async exists
        var originalContext = SynchronizationContext.Current;
        var blockingContext = new BlockingMockSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(blockingContext);

        try
        {
            File.WriteAllText(_absoluteConfigFilePath, JsonConvert.SerializeObject(new AppConfig
            {
                Music = new MusicConfig { PreparationMs = 2500 }
            }));

            // Construct ConfigService directly on thread with blocking context.
            // If any sync-over-async or context dispatch occurs, it throws or deadlocks.
            var service = new ConfigService(configRelativePath: _relativeConfigFileName);
            Assert.Equal(2500, service.Config.Music.PreparationMs);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    [Fact]
    public async Task InitializeAsync_And_SaveAsync_SucceedsAndPreservesSettings()
    {
        var service = new ConfigService(configRelativePath: _relativeConfigFileName);
        service.Config.Music.PreparationMs = 1234;
        service.Config.AutoPick.Whitelist = new System.Collections.Generic.List<string> { "拾取", "采集" };

        await service.SaveAsync();
        Assert.True(File.Exists(_absoluteConfigFilePath));

        // Reload via InitializeAsync
        await service.InitializeAsync();
        Assert.Equal(1234, service.Config.Music.PreparationMs);
        Assert.Contains("采集", service.Config.AutoPick.Whitelist);
    }

    [Fact]
    public void CorruptedJson_FallsBackToDefaultsGracefully()
    {
        File.WriteAllText(_absoluteConfigFilePath, "{ invalid-json: true, broken... }");

        var service = new ConfigService(configRelativePath: _relativeConfigFileName);
        Assert.NotNull(service.Config);
        Assert.NotNull(service.Config.Music);
        Assert.NotNull(service.Config.AutoPick);
    }

    private sealed class BlockingMockSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // If any async method without ConfigureAwait(false) tries to post continuation back,
            // this mock immediately tracks or blocks to simulate a busy UI thread.
            throw new InvalidOperationException("Post was called on SynchronizationContext during synchronous operation!");
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            throw new InvalidOperationException("Send was called on SynchronizationContext during synchronous operation!");
        }
    }
}
