using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.View.Pages;
using BetterPetitPlanet.ViewModel.Pages;
using Wpf.Ui.Controls;

namespace BetterPetitPlanet.Test;

public sealed class ItemNameSettingsRenderingTest
{
    [ItemSettingsRenderFact]
    public async Task Render_DefaultToggleAndTwoWayBindingWithoutStartingApplication()
    {
        string root = FindRepositoryRoot();
        string appXaml = await File.ReadAllTextAsync(Path.Combine(root, "BetterPetitPlanet", "App.xaml"));
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(RenderAndVerify(appXaml)); }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true, Name = "Item settings render validation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        byte[] png = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        string output = Path.Combine(root, "Test", "BetterPetitPlanet.Test", "Validation", "item-names", "settings.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllBytesAsync(output, png);
        Assert.True(png.Length > 1024, "The settings screenshot must contain rendered page content.");
    }

    private static byte[] RenderAndVerify(string appXaml)
    {
        // A plain Application avoids App's host, startup handlers, hotkeys and OnExit.
        // Run this opt-in test alone: WPF Application is a process-wide singleton.
        Assert.Null(Application.Current);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources = ReadApplicationResources(appXaml);
        var service = new FakeConfigService();
        using var viewModel = new TriggerSettingsPageViewModel(service);
        var page = new TriggerSettingsPage(viewModel)
        {
            Width = 1050,
            Height = 1500,
            Background = (Brush)application.FindResource("ApplicationBackgroundBrush")
        };
        using var source = new HwndSource(new HwndSourceParameters("Item settings validation")
        {
            Width = 1050,
            Height = 1500,
            WindowStyle = 0 // No WS_VISIBLE: provides Loaded/layout without showing UI.
        });
        source.RootVisual = page;
        page.Measure(new Size(page.Width, page.Height));
        page.Arrange(new Rect(0, 0, page.Width, page.Height));
        page.UpdateLayout();
        page.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        FinishOpeningAnimation();
        page.Measure(new Size(page.Width, page.Height));
        page.Arrange(new Rect(0, 0, page.Width, page.Height));
        page.UpdateLayout();

        var toggle = Assert.Single(Descendants(page).OfType<ToggleSwitch>()
            .Where(control => AutomationProperties.GetName(control) == "识别物品名称并记录日志"));
        Assert.True(toggle.IsVisible);
        Assert.True(toggle.ActualWidth > 0 && toggle.ActualHeight > 0);
        var card = Assert.Single(Descendants(page).OfType<CardExpander>());
        var header = Assert.IsAssignableFrom<FrameworkElement>(card.Template.FindName("ExpanderToggleButton", card));
        var headerBounds = header.TransformToAncestor(page).TransformBounds(new Rect(header.RenderSize));
        var toggleBounds = toggle.TransformToAncestor(page).TransformBounds(new Rect(toggle.RenderSize));
        Assert.True(toggleBounds.Top >= headerBounds.Bottom, "The item-name toggle must render below the card header after expansion.");
        Assert.True(new Rect(page.RenderSize).Contains(toggleBounds), "The item-name toggle must be fully inside the rendered page.");
        Assert.False(toggle.IsChecked);
        Assert.False(service.Config.AutoPick.ItemNameRecognition.Enabled);
        Assert.False(service.Config.AutoPick.Enabled);

        var bitmap = new RenderTargetBitmap(1050, 1500, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        page.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Assert.True(viewModel.ItemNameRecognitionEnabled);
        Assert.True(service.Config.AutoPick.ItemNameRecognition.Enabled);
        Assert.True(service.SaveCount > 0);
        Assert.False(viewModel.AutoPickEnabled);
        Assert.False(service.Config.AutoPick.Enabled);
        Assert.Empty(application.Windows.Cast<Window>());
        return stream.ToArray();
    }

    private static void FinishOpeningAnimation()
    {
        // Wpf.Ui animates an expanded card after Loaded; keep render ticks flowing
        // before taking a static screenshot, without running the application.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static ResourceDictionary ReadApplicationResources(string appXaml)
    {
        var document = XDocument.Parse(appXaml);
        var application = document.Root!;
        var resources = new XElement(application.Elements().Single().Elements().Single());
        foreach (var declaration in application.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            string value = declaration.Value;
            if (value.StartsWith("clr-namespace:", StringComparison.Ordinal) && !value.Contains(";assembly="))
            {
                value += ";assembly=" + typeof(TriggerSettingsPage).Assembly.GetName().Name;
                foreach (var element in resources.DescendantsAndSelf().Where(element => element.Name.NamespaceName == declaration.Value))
                    element.Name = XName.Get(element.Name.LocalName, value);
            }
            resources.SetAttributeValue(declaration.Name, value);
        }
        return (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BetterPetitPlanet", "BetterPetitPlanet.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Run rendering validation from the repository's test build output.");
    }

    private sealed class FakeConfigService : IConfigService
    {
        public AppConfig Config { get; } = new();
        public int SaveCount { get; private set; }
        public void Save() => SaveCount++;
        public void Reload() { }
    }

    public sealed class ItemSettingsRenderFactAttribute : FactAttribute
    {
        public ItemSettingsRenderFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PETIT_RENDER_ITEM_SETTINGS") != "1")
                Skip = "Set PETIT_RENDER_ITEM_SETTINGS=1 and run only ItemNameSettingsRenderingTest in an isolated test process.";
        }
    }
}
