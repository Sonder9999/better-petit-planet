using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.ViewModel.Pages;
using Wpf.Ui.Controls;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicPlaybackModeTest
{
    private class FakeConfigService : IConfigService
    {
        public AppConfig Config { get; set; } = new AppConfig();
        public void Save() { }
        public void Reload() { }
    }

    private static (FakeConfigService, MusicLibraryService, MusicPlaybackService) CreateServices()
    {
        var configService = new FakeConfigService();
        var processDetector = new GameProcessDetector(configService);
        var transports = new KeyInputTransports();
        var playbackService = new MusicPlaybackService(configService, processDetector, transports);
        var scoreParser = new PetitScoreParser();
        var libraryService = new MusicLibraryService(scoreParser, configService);
        return (configService, libraryService, playbackService);
    }

    [Fact]
    public void DefaultPlaybackMode_ShouldBePlayOnce()
    {
        var config = new AppConfig();
        Assert.Equal(MusicPlaybackMode.PlayOnce, config.Music.PlaybackMode);
    }

    [Fact]
    public void MusicPageViewModel_InitialMode_ShouldBePlayOnce()
    {
        var (configService, libraryService, playbackService) = CreateServices();
        var vm = new MusicPageViewModel(configService, libraryService, playbackService);

        Assert.Equal(MusicPlaybackMode.PlayOnce, vm.PlaybackMode);
        Assert.Equal(SymbolRegular.ArrowRight24, vm.PlaybackModeSymbol);
        Assert.Contains("弹完停止", vm.PlaybackModeToolTip);
    }

    [Fact]
    public void CyclePlaybackMode_ShouldCycleThroughAllFourModes()
    {
        var (configService, libraryService, playbackService) = CreateServices();
        var vm = new MusicPageViewModel(configService, libraryService, playbackService);

        // 1. Initial: PlayOnce
        Assert.Equal(MusicPlaybackMode.PlayOnce, vm.PlaybackMode);
        Assert.Equal(SymbolRegular.ArrowRight24, vm.PlaybackModeSymbol);

        // 2. Cycle -> ListLoop
        vm.CyclePlaybackModeCommand.Execute(null);
        Assert.Equal(MusicPlaybackMode.ListLoop, vm.PlaybackMode);
        Assert.Equal(SymbolRegular.ArrowRepeatAll24, vm.PlaybackModeSymbol);
        Assert.Contains("列表循环", vm.PlaybackModeToolTip);

        // 3. Cycle -> SingleLoop
        vm.CyclePlaybackModeCommand.Execute(null);
        Assert.Equal(MusicPlaybackMode.SingleLoop, vm.PlaybackMode);
        Assert.Equal(SymbolRegular.ArrowRepeat124, vm.PlaybackModeSymbol);
        Assert.Contains("单曲循环", vm.PlaybackModeToolTip);

        // 4. Cycle -> Shuffle
        vm.CyclePlaybackModeCommand.Execute(null);
        Assert.Equal(MusicPlaybackMode.Shuffle, vm.PlaybackMode);
        Assert.Equal(SymbolRegular.ArrowShuffle24, vm.PlaybackModeSymbol);
        Assert.Contains("随机播放", vm.PlaybackModeToolTip);

        // 5. Cycle -> back to PlayOnce
        vm.CyclePlaybackModeCommand.Execute(null);
        Assert.Equal(MusicPlaybackMode.PlayOnce, vm.PlaybackMode);
        Assert.Equal(SymbolRegular.ArrowRight24, vm.PlaybackModeSymbol);
        Assert.Contains("弹完停止", vm.PlaybackModeToolTip);
    }

    [Fact]
    public void GetRandomSong_ShouldReturnValidSong()
    {
        var (configService, libraryService, playbackService) = CreateServices();
        // When empty, returns null
        Assert.Null(libraryService.GetRandomSong());
    }
}
