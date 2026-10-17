using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View.Controls.Music;
using BetterPetitPlanet.ViewModel.Windows;

namespace BetterPetitPlanet.Test;

public sealed class MusicOverlaySettingsLayoutTest
{
    [Fact]
    public void NarrowWindow_StacksSkinListBelowPreviewWithoutHorizontalOverflow() => MusicRenderingTestHost.Run(() =>
    {
        using var viewModel = new MusicOverlaySettingsViewModel(
            new MemoryConfig(),
            previewSourceFactory: () => new MusicPreviewSource(() => 0),
            dispatcher: Dispatcher.CurrentDispatcher,
            skinLoader: id => Task.FromResult(new MusicSkin { Id = id }));
        var control = new MusicOverlaySettingsControl { Width = 320, ViewModel = viewModel };
        using var host = MusicRenderingTestHost.Mount(control);

        var preview = (FrameworkElement)control.FindName("PlayfieldPreview");
        var skinList = (FrameworkElement)control.FindName("SkinListBox");
        var previewBounds = GetBounds(preview, control);
        var skinListBounds = GetBounds(skinList, control);

        AssertWithinWidth(previewBounds, control.ActualWidth, "preview");
        AssertWithinWidth(skinListBounds, control.ActualWidth, "skin list");
        Assert.True(
            skinListBounds.Top >= previewBounds.Bottom,
            $"Expected the skin list below the preview, but preview={previewBounds} and list={skinListBounds}.");

        var visibleItems = GetVisualDescendants(skinList)
            .OfType<ListBoxItem>()
            .Where(item => item.IsVisible && item.ActualWidth > 0 && item.ActualHeight > 0)
            .ToArray();
        Assert.NotEmpty(visibleItems);

        foreach (var item in visibleItems)
        {
            AssertWithinWidth(GetBounds(item, control), control.ActualWidth, "visible skin item");

            var card = GetVisualDescendants(item)
                .OfType<Border>()
                .SingleOrDefault(border => border.Name == "CardBorder");
            Assert.NotNull(card);
            AssertWithinWidth(GetBounds(card!, control), control.ActualWidth, "skin card");

            foreach (var content in GetVisualDescendants(card!)
                         .OfType<FrameworkElement>()
                         .Where(element => element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0))
            {
                AssertWithinWidth(GetBounds(content, control), control.ActualWidth,
                    $"visible skin card content ({content.GetType().Name})");
            }
        }
    });

    private static Rect GetBounds(FrameworkElement element, FrameworkElement ancestor)
    {
        var origin = element.TransformToAncestor(ancestor).Transform(new Point());
        return new Rect(origin, element.RenderSize);
    }

    private static IEnumerable<DependencyObject> GetVisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in GetVisualDescendants(child)) yield return descendant;
        }
    }

    private static void AssertWithinWidth(Rect bounds, double width, string elementName) => Assert.True(
        bounds.Left >= 0 && bounds.Right <= width,
        $"Expected the {elementName} to fit the {width} DIP control width, but bounds={bounds}.");

    private sealed class MemoryConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
    }
}
