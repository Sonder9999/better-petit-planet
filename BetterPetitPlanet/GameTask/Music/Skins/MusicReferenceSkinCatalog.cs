using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Rendering;

namespace BetterPetitPlanet.GameTask.Music.Skins;

public static class MusicReferenceSkinCatalog
{
    private static readonly Lazy<ReferenceSkinDefinition[]> Catalog = new(() =>
        JsonSerializer.Deserialize<ReferenceSkinDefinition[]>(File.ReadAllText(Path.Combine(AssetRoot, "catalog.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []);

    internal static string AssetRoot => Path.GetDirectoryName(MusicDefaultSkin.ResolveAssetPath("Reference/catalog.json"))
        ?? throw new FileNotFoundException("内置音游皮肤资源缺失");
    public static IReadOnlyList<ReferenceSkinDefinition> Definitions => Catalog.Value;
    public static string SelectionId(ReferenceSkinDefinition definition) => definition.Id == "nahida" ? "default" : "builtin:" + definition.Id;
    public static ReferenceSkinDefinition? Find(string id) => Definitions.FirstOrDefault(definition =>
        SelectionId(definition).Equals(id, StringComparison.OrdinalIgnoreCase));

    private static readonly Lazy<MusicSkinInfo[]> SkinDescriptors = new(() => Definitions.Where(definition => definition.Id != "nahida")
        .Select(definition => new MusicSkinInfo(SelectionId(definition), definition.DisplayName,
            "music_game_clear", "原版背景、音符与特效", IsBuiltIn: true, IconSource: LoadCover(definition),
            CharacterName: definition.Character, Region: definition.Region)).ToArray());

    public static IEnumerable<MusicSkinInfo> Descriptors => SkinDescriptors.Value;

    private static BitmapSource? LoadCover(ReferenceSkinDefinition definition)
    {
        if (definition.PreviewTexture == null) return null;
        try
        {
            using var stream = File.OpenRead(Path.Combine(AssetRoot, definition.PreviewTexture));
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or FileFormatException)
        {
            // Covers are optional; a missing cover must not hide a playable theme.
            return null;
        }
    }

    internal static MusicSkin Load(ReferenceSkinDefinition definition, MusicSkinDecodeSize size, CancellationToken cancellationToken)
    {
        var urls = definition.Background.SelectMany(layer => new[] { layer.Url, layer.Fit.WingUrl, layer.Fit.RightUrl })
            .Concat(definition.NoteTextures).Append(definition.JudgePointTexture).Where(url => url != null).Distinct();
        var images = new Dictionary<string, ReferenceSkinImage>();
        foreach (string url in urls!)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(Path.Combine(AssetRoot, url));
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            double ratio = Math.Min(1, Math.Min((double)Math.Max(size.Width, 512) / width, (double)Math.Max(size.Height, 512) / height));
            bitmap.DecodePixelWidth = Math.Max(1, (int)(width * ratio));
            bitmap.DecodePixelHeight = Math.Max(1, (int)(height * ratio));
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            images.Add(url, new(bitmap, width, height));
        }
        cancellationToken.ThrowIfCancellationRequested();
        ReferenceFrameStore.Validate(definition);
        cancellationToken.ThrowIfCancellationRequested();
        return new MusicSkin
        {
            Id = SelectionId(definition), DisplayName = definition.DisplayName, Author = "music_game_clear",
            Reference = new ReferenceSkinResources(definition, images),
            TrackBackgroundBrush = Brush(definition.Backdrop),
            TrackLaneGuidePen = Pen(0xffffff, .22, 1), TrackCenterGuidePen = Pen(0xffffff, .42, 1),
            NoteHoldBitmap = images[definition.NoteTextures[0]].Bitmap,
            NoteTapBitmap = images[definition.NoteTextures[1]].Bitmap,
            NotePointBitmap = images[definition.JudgePointTexture].Bitmap,
            HoldBeamBitmap = MusicHoldBeamTexture.Create(definition.HoldColor ?? definition.Accent.Primary),
            HitEffectDurationMs = definition.Hit.DurationMs,
        };
    }

    private static SolidColorBrush Brush(uint rgb, double alpha = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze(); return brush;
    }
    private static Pen Pen(uint rgb, double alpha, double width) { var pen = new Pen(Brush(rgb, alpha), width); pen.Freeze(); return pen; }
}
