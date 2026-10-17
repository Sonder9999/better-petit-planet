using BetterPetitPlanet.Core.Config;
using System.Security.Cryptography;
using System.Text.Json;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View.Controls.Music;
using BetterPetitPlanet.ViewModel.Windows;

namespace BetterPetitPlanet.Test;

public sealed class MusicDefaultSkinAssetTest
{
    [Fact]
    public void DefaultSkin_LoadsNahidaNotesInsteadOfRectangleFallback()
    {
        Assert.NotNull(MusicDefaultSkin.NoteTapBitmap);
        Assert.NotNull(MusicDefaultSkin.NoteHoldBitmap);
        Assert.Equal(120, MusicDefaultSkin.NoteTapBitmap.PixelWidth);
        Assert.Equal(120, MusicDefaultSkin.NoteHoldBitmap.PixelHeight);
        Assert.True(MusicDefaultSkin.NoteTapBitmap.IsFrozen);
        Assert.NotNull(MusicDefaultSkin.BackgroundBitmap);
        Assert.NotNull(MusicDefaultSkin.BackgroundGradientBitmap);
        Assert.NotNull(MusicDefaultSkin.BackgroundArchBitmap);
        Assert.NotNull(MusicDefaultSkin.BackgroundWingBitmap);
        Assert.NotNull(MusicDefaultSkin.BackgroundBottomBitmap);
        Assert.True(MusicDefaultSkin.HitAtlasFrames == null || MusicDefaultSkin.HitAtlasFrames.Length == 64);
        Assert.True(HoldEffectRenderer.HasSource);
        Assert.NotNull(MusicDefaultSkin.PreviewBitmap);
        Assert.Null(MusicDefaultSkin.ResourceDiagnostic);
    }

    [Fact]
    public void CanonicalDefaultAssets_AreCopiedAndMatchTheirManifest()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Assets", "MusicOverlay", "NativeDefault");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var entries = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(14, entries.Length);
        foreach (var entry in entries)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, entry.GetProperty("path").GetString()!));
            Assert.Equal(entry.GetProperty("bytes").GetInt64(), bytes.Length);
            Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }

    [Fact]
    public void GpuDraftPreview_UsesSupportedEffectsWithoutChangingSavedPreferences() => MusicRenderingTestHost.Run(() =>
    {
        var config = new MemoryConfig();
        config.Config.MusicOverlay.Backend = MusicOverlayBackend.Gpu;
        using var vm = new MusicOverlaySettingsViewModel(config, skinLoader: id => Task.FromResult(new MusicSkin { Id = id }));
        var control = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(control);
        MusicRenderingTestHost.Drain();
        var preview = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        Assert.True(vm.UseGpuRenderer);
        Assert.True(preview.EffectsConfig.HitBursts);
        Assert.True(preview.EffectsConfig.HoldGlow);
        Assert.Equal(vm.HitBursts, preview.EffectsConfig.HitBursts);
        Assert.Equal(vm.HoldGlow, preview.EffectsConfig.HoldGlow);
        Assert.True(preview.EffectsConfig.ShowBackgroundDeco);
        Assert.True(vm.HitBursts);
        Assert.True(vm.HoldGlow);
        Assert.True(config.Config.MusicOverlay.Effects.HitBursts);
        Assert.True(config.Config.MusicOverlay.Effects.HoldGlow);
        Assert.Contains("GPU 外观预览：原生默认皮肤完整特效", vm.PreviewBackendStatus);

        vm.UseGpuRenderer = false;
        MusicRenderingTestHost.Drain();
        Assert.False(vm.UseGpuRenderer);
        Assert.True(preview.EffectsConfig.HitBursts);
        Assert.True(preview.EffectsConfig.HoldGlow);
        Assert.Equal(vm.HitBursts, preview.EffectsConfig.HitBursts);
        Assert.Equal(vm.HoldGlow, preview.EffectsConfig.HoldGlow);
        Assert.Equal("WPF 外观预览", vm.PreviewBackendStatus);

        vm.HitBursts = false;
        MusicRenderingTestHost.Drain();
        Assert.False(preview.EffectsConfig.HitBursts);
        Assert.False(vm.HitBursts);

        vm.UseGpuRenderer = true;
        MusicRenderingTestHost.Drain();
        Assert.True(vm.UseGpuRenderer);
        Assert.False(preview.EffectsConfig.HitBursts);
        Assert.True(preview.EffectsConfig.HoldGlow);
        Assert.Equal(vm.HitBursts, preview.EffectsConfig.HitBursts);
        Assert.Equal(vm.HoldGlow, preview.EffectsConfig.HoldGlow);
        Assert.Contains("GPU 外观预览：原生默认皮肤完整特效", vm.PreviewBackendStatus);
    });

    [Theory]
    [InlineData("default", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("builtin:shougun", true)]
    [InlineData("builtin:nodkrai", true)]
    [InlineData("builtin:durin", true)]
    [InlineData("builtin:furina", true)]
    [InlineData("builtin:neuvillette", true)]
    [InlineData("builtin:unknown_fallback", false)]
    [InlineData("custom_skin_1", true)]
    public void IsGpuSupportedSkin_CorrectlyIdentifiesSupportedSkins(string? skinId, bool expected)
    {
        Assert.Equal(expected, MusicOverlayPresenter.IsGpuSupportedSkin(skinId));
    }

    [Fact]
    public void BuiltinReferenceSkin_PreviewBackendStatus_ReflectsSupportAndFallback() => MusicRenderingTestHost.Run(() =>
    {
        var config = new MemoryConfig();
        config.Config.MusicOverlay.Backend = MusicOverlayBackend.Gpu;
        using var vm = new MusicOverlaySettingsViewModel(config, skinLoader: id => Task.FromResult(new MusicSkin { Id = id }));

        // Default skin
        Assert.Equal("default", vm.SelectedSkinId);
        Assert.Equal("GPU 外观预览：原生默认皮肤完整特效（含命中爆发、长按粒子及按键反馈）。", vm.PreviewBackendStatus);

        // C1 reference skin: builtin:shougun
        vm.SelectedSkinId = "builtin:shougun";
        Assert.Equal("GPU 外观预览：内置参考皮肤完整特效（含多层背景、命中爆发、长按粒子及按键反馈）。", vm.PreviewBackendStatus);

        // C1 reference skin: builtin:nodkrai (月与乐园)
        vm.SelectedSkinId = "builtin:nodkrai";
        Assert.Equal("GPU 外观预览：内置参考皮肤完整特效（含多层背景、命中爆发、长按粒子及按键反馈）。", vm.PreviewBackendStatus);

        // C2 reference skin: builtin:furina
        vm.SelectedSkinId = "builtin:furina";
        Assert.Equal("GPU 外观预览：内置参考皮肤完整特效（含多层背景、命中爆发、长按粒子及按键反馈）。", vm.PreviewBackendStatus);

        // C2 reference skin: builtin:neuvillette
        vm.SelectedSkinId = "builtin:neuvillette";
        Assert.Equal("GPU 外观预览：内置参考皮肤完整特效（含多层背景、命中爆发、长按粒子及按键反馈）。", vm.PreviewBackendStatus);

        // Unknown builtin reference skin: builtin:unknown_fallback
        vm.SelectedSkinId = "builtin:unknown_fallback";
        Assert.Equal("此皮肤暂由 WPF 呈现，预览使用 WPF 效果。", vm.PreviewBackendStatus);

        // Custom skin
        vm.SelectedSkinId = "custom_test";
        Assert.Equal("GPU 外观预览：自定义皮肤完整特效（含命中爆发、长按粒子及按键反馈）。", vm.PreviewBackendStatus);

        // When WPF backend is selected
        vm.UseGpuRenderer = false;
        Assert.Equal("WPF 外观预览", vm.PreviewBackendStatus);
        vm.SelectedSkinId = "builtin:shougun";
        Assert.Equal("WPF 外观预览", vm.PreviewBackendStatus);
    });

    private sealed class MemoryConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
    }
}
