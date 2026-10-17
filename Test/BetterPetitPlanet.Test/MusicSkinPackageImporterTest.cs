using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.ViewModel.Windows;

namespace BetterPetitPlanet.Test;

public sealed class MusicSkinPackageImporterTest : IDisposable
{
    private sealed class MemoryConfigService : IConfigService
    {
        public AppConfig Config { get; } = new();
        public int SaveCount { get; private set; }
        public void Save() => SaveCount++;
        public Task SaveAsync() { SaveCount++; return Task.CompletedTask; }
        public Task SaveMusicOverlayAsync(MusicOverlayConfig settings)
        {
            settings.Validate();
            Config.MusicOverlay.ApplySettingsFrom(settings);
            return SaveAsync();
        }
        public void Reload() { }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bpp-music-skin-import-tests", Guid.NewGuid().ToString("N"));
    private string SkinsDirectory => Path.Combine(_root, "data", "music-skins");

    public MusicSkinPackageImporterTest() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_InstallsValidatedPackageFromSupportedRootLayouts(bool wrapped)
    {
        const string id = "zip-fixture";
        string archivePath = CreateArchive(archive =>
            AddTextEntry(archive, wrapped ? $"{id}/skin.json" : "skin.json", MinimalManifest(id)));
        var repository = new MusicSkinRepository(SkinsDirectory);
        var importer = new MusicSkinPackageImporter(repository);

        MusicSkinPackageImportResult result = await importer.ImportAsync(archivePath);

        Assert.Equal(id, result.Id);
        Assert.Equal("ZIP Fixture", result.DisplayName);
        Assert.Contains(repository.GetAvailableSkins(), skin => skin.Id == "default");
        Assert.Contains(repository.GetAvailableSkins(), skin => skin.Id == id);
        Assert.True(File.Exists(Path.Combine(SkinsDirectory, id, "skin.json")));
        AssertNoStagingDirectories();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"id\":\"bad\",\"displayName\":\"Bad\",\"capabilities\":[\"native-v1\"],\"contentHashes\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"id\":\"../escape\",\"displayName\":\"Bad\",\"capabilities\":[\"native-v1\"],\"contentHashes\":{}}")]
    public async Task Import_RejectsDamagedOrInvalidManifestAndCleansStaging(string manifest)
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", manifest));
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Empty(Directory.GetDirectories(SkinsDirectory));
        AssertNoStagingDirectories();
    }

    [Theory]
    [InlineData("../skin.json")]
    [InlineData("..\\skin.json")]
    [InlineData("/skin.json")]
    [InlineData("C:/skin.json")]
    public async Task Import_RejectsZipSlipBeforeWritingOutsideStaging(string entryPath)
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, entryPath, MinimalManifest("escape")));
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));
        string escapedPath = Path.Combine(Path.GetDirectoryName(SkinsDirectory)!, "skin.json");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("路径", error.Message);
        Assert.False(File.Exists(escapedPath));
        AssertNoStagingDirectories();
    }

    [Theory]
    [InlineData("COM¹.json")]
    [InlineData("COM².json")]
    [InlineData("COM³.json")]
    [InlineData("LPT¹.json")]
    [InlineData("LPT².json")]
    [InlineData("LPT³.json")]
    public async Task Import_RejectsWindowsSuperscriptDeviceNames(string entryPath)
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, entryPath, "{}"));
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("不安全路径", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsCaseInsensitiveDuplicatePaths()
    {
        string archivePath = CreateArchive(archive =>
        {
            AddTextEntry(archive, "skin.json", MinimalManifest("duplicate"));
            AddTextEntry(archive, "SKIN.JSON", MinimalManifest("duplicate"));
        });
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("重复", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsPathDepthAboveDirectoryLimitBeforeStaging()
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "a/b/c/d/leaf.json", "{}"));
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory,
            limits: new MusicSkinResourceLimits { MaxFiles = 2 }));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("路径深度", error.Message);
        AssertNoStagingDirectories();
    }

    [Theory]
    [InlineData("package-bytes")]
    [InlineData("file-count")]
    [InlineData("manifest-bytes")]
    public async Task Import_EnforcesConfiguredLimitsDuringExtraction(string kind)
    {
        var limits = kind switch
        {
            "package-bytes" => new MusicSkinResourceLimits { MaxPackageBytes = 16 },
            "file-count" => new MusicSkinResourceLimits { MaxFiles = 1 },
            _ => new MusicSkinResourceLimits { MaxManifestBytes = 16 }
        };
        string archivePath = CreateArchive(archive =>
        {
            AddTextEntry(archive, "skin.json", MinimalManifest("too-large"));
            if (kind == "file-count") AddTextEntry(archive, "extra.json", "{}");
        });
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory, limits: limits));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("限", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_EnforcesActualDecompressedByteLimitForAccuratelyDeclaredCompressedEntry()
    {
        string payload = new('x', 4096);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        string manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = "compressed-bomb",
            displayName = "Compressed Bomb",
            capabilities = new[] { "native-v1" },
            contentHashes = new Dictionary<string, string> { ["payload.json"] = hash }
        });
        string archivePath = CreateArchive(archive =>
        {
            AddTextEntry(archive, "skin.json", manifest);
            AddTextEntry(archive, "payload.json", payload);
        });
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory,
            limits: new MusicSkinResourceLimits { MaxPackageBytes = 512 }));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("解压总字节", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsOversizedArchiveBeforeParsingCentralDirectory()
    {
        string archivePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = File.Create(archivePath))
            archive.SetLength(16L * 1024 * 1024 + 2);
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory,
            limits: new MusicSkinResourceLimits { MaxPackageBytes = 1 }));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("元数据余量", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsCentralDirectoryMetadataOverLimitBeforeParsing()
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", MinimalManifest("oversized-central-dir")));
        SetCentralDirectorySize(archivePath, 16 * 1024 * 1024 + 1);
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("中央目录", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsCentralDirectoryOutsideArchiveBounds()
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", MinimalManifest("bad-central-offset")));
        SetCentralDirectoryOffset(archivePath, 1_000_000);
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("中央目录边界", error.InnerException!.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public void ZipArchive_OnNet8ReadsEntryCountDespiteUnderstatedCentralDirectorySize()
    {
        const int payloadEntries = 300;
        string archivePath = CreateLargeCentralDirectoryArchive(payloadEntries);
        uint actualSize = GetCentralDirectorySize(archivePath);
        Assert.True(actualSize > 16 * 1024 * 1024, $"Expected a large central directory, got {actualSize} bytes");
        SetCentralDirectorySize(archivePath, 1);

        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(payloadEntries + 1, archive.Entries.Count);
    }

    [Fact]
    public async Task Import_RejectsPhysicalCentralDirectoryOverLimitWhenEndRecordUnderstatesIt()
    {
        string archivePath = CreateLargeCentralDirectoryArchive(300);
        Assert.True(GetCentralDirectorySize(archivePath) > 16 * 1024 * 1024);
        SetCentralDirectorySize(archivePath, 1);
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("中央目录", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsTooManyCentralDirectoryEntriesBeforeParsing()
    {
        string archivePath = CreateArchive(archive =>
        {
            AddTextEntry(archive, "skin.json", MinimalManifest("too-many-entries"));
            AddTextEntry(archive, "one.json", "{}");
            AddTextEntry(archive, "two.json", "{}");
        });
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory,
            limits: new MusicSkinResourceLimits { MaxFiles = 1 }));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("条目数量", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsEndOfDirectorySignatureAmbiguityInZipComment()
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", MinimalManifest("comment-signature")));
        AddFakeEndOfDirectorySignatureComment(archivePath);
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("签名存在歧义", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_AcceptsZip64EndOfDirectoryRecords()
    {
        const string id = "zip64-fixture";
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", MinimalManifest(id)));
        ConvertToZip64(archivePath);
        var repository = new MusicSkinRepository(SkinsDirectory);
        var importer = new MusicSkinPackageImporter(repository);

        MusicSkinPackageImportResult result = await importer.ImportAsync(archivePath);

        Assert.Equal(id, result.Id);
        Assert.Contains(repository.GetAvailableSkins(), skin => skin.Id == id);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsExistingIdWithoutOverwritingInstalledPackage()
    {
        const string id = "already-installed";
        string existingPackage = Path.Combine(SkinsDirectory, id);
        Directory.CreateDirectory(existingPackage);
        string sentinelPath = Path.Combine(existingPackage, "keep.txt");
        await File.WriteAllTextAsync(sentinelPath, "preserve existing files");
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", MinimalManifest(id)));
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains(id, error.Message);
        Assert.Equal("preserve existing files", await File.ReadAllTextAsync(sentinelPath));
        AssertNoStagingDirectories();
    }

    [Theory]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public async Task Import_RejectsReservedBuiltInDefaultId(string id)
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", MinimalManifest(id)));
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("保留", error.Message);
        Assert.False(Directory.Exists(Path.Combine(SkinsDirectory, id)));
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_RejectsSymlinkEntriesAndCleansStaging()
    {
        string archivePath = CreateArchive(archive =>
        {
            ZipArchiveEntry entry = archive.CreateEntry("skin.json");
            entry.ExternalAttributes = unchecked((int)(0xA000 << 16));
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(MinimalManifest("linked"));
        });
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(archivePath));

        Assert.Contains("链接", error.Message);
        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task Import_CancellationLeavesNoTemporaryDirectory()
    {
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", MinimalManifest("cancelled")));
        var importer = new MusicSkinPackageImporter(new MusicSkinRepository(SkinsDirectory));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ImportAsync(archivePath, cancellation.Token));

        AssertNoStagingDirectories();
    }

    [Fact]
    public async Task ViewModelImport_RefreshesAndPreviewsAsUnappliedDraft()
    {
        const string id = "draft-import";
        string archivePath = CreateArchive(archive => AddTextEntry(archive, "skin.json", MinimalManifest(id)));
        var config = new MemoryConfigService();
        var repository = new MusicSkinRepository(SkinsDirectory);
        using var viewModel = new MusicOverlaySettingsViewModel(config, skinRepository: repository);

        await viewModel.ImportSkinPackageAsync(archivePath);

        Assert.Equal(id, viewModel.SelectedSkinId);
        Assert.Contains(viewModel.AvailableSkins, skin => skin.Id == id);
        Assert.Equal(id, viewModel.ActiveSkin.Id);
        Assert.True(viewModel.IsDirty);
        Assert.Equal("default", config.Config.MusicOverlay.SkinId);
        Assert.Equal(0, config.SaveCount);
        Assert.Contains("已导入", viewModel.ImportStatusMessage);
    }

    private string CreateArchive(Action<ZipArchive> addEntries)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        addEntries(archive);
        return path;
    }

    private string CreateLargeCentralDirectoryArchive(int payloadEntries)
    {
        const int fileNameLength = 60_000;
        return CreateArchive(archive =>
        {
            AddTextEntry(archive, "skin.json", MinimalManifest("large-central-directory"));
            for (int index = 0; index < payloadEntries; index++)
            {
                string name = $"entry-{index:D4}-" + new string('x', fileNameLength - 16) + ".json";
                AddTextEntry(archive, name, "{}");
            }
        });
    }

    private static void AddTextEntry(ZipArchive archive, string path, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static int FindEndOfDirectoryOffset(byte[] archive)
    {
        for (int offset = archive.Length - 22; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(offset, 4)) == 0x06054b50 &&
                offset + 22 + BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(offset + 20, 2)) == archive.Length)
                return offset;
        }
        throw new InvalidOperationException("ZIP end-of-directory record not found");
    }

    private static void SetCentralDirectorySize(string archivePath, uint size)
    {
        byte[] archive = File.ReadAllBytes(archivePath);
        int offset = FindEndOfDirectoryOffset(archive);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(offset + 12, 4), size);
        File.WriteAllBytes(archivePath, archive);
    }

    private static uint GetCentralDirectorySize(string archivePath)
    {
        byte[] archive = File.ReadAllBytes(archivePath);
        int offset = FindEndOfDirectoryOffset(archive);
        return BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(offset + 12, 4));
    }

    private static void SetCentralDirectoryOffset(string archivePath, uint offsetValue)
    {
        byte[] archive = File.ReadAllBytes(archivePath);
        int offset = FindEndOfDirectoryOffset(archive);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(offset + 16, 4), offsetValue);
        File.WriteAllBytes(archivePath, archive);
    }

    private static void AddFakeEndOfDirectorySignatureComment(string archivePath)
    {
        byte[] archive = File.ReadAllBytes(archivePath);
        int eocdOffset = FindEndOfDirectoryOffset(archive);
        const int commentLength = 22;
        byte[] extended = new byte[archive.Length + commentLength];
        Buffer.BlockCopy(archive, 0, extended, 0, archive.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(extended.AsSpan(eocdOffset + 20, 2), commentLength);
        BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(archive.Length, 4), 0x06054b50);
        File.WriteAllBytes(archivePath, extended);
    }

    private static void ConvertToZip64(string archivePath)
    {
        byte[] archive = File.ReadAllBytes(archivePath);
        int eocdOffset = FindEndOfDirectoryOffset(archive);
        ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(eocdOffset + 20, 2));
        ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(eocdOffset + 10, 2));
        uint centralSize = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(eocdOffset + 12, 4));
        uint centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(eocdOffset + 16, 4));
        const int zip64RecordLength = 56;
        const int locatorLength = 20;
        const int eocdLength = 22;
        byte[] extended = new byte[eocdOffset + zip64RecordLength + locatorLength + eocdLength + commentLength];
        Buffer.BlockCopy(archive, 0, extended, 0, eocdOffset);

        Span<byte> zip64 = extended.AsSpan(eocdOffset, zip64RecordLength);
        BinaryPrimitives.WriteUInt32LittleEndian(zip64, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64[12..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64[14..], 45);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[24..], entries);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[32..], entries);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[40..], centralSize);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64[48..], centralOffset);

        int locatorOffset = eocdOffset + zip64RecordLength;
        Span<byte> locator = extended.AsSpan(locatorOffset, locatorLength);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)eocdOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);

        int extendedEocdOffset = locatorOffset + locatorLength;
        Span<byte> extendedEocd = extended.AsSpan(extendedEocdOffset, eocdLength);
        BinaryPrimitives.WriteUInt32LittleEndian(extendedEocd, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(extendedEocd[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(extendedEocd[10..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(extendedEocd[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(extendedEocd[16..], uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(extendedEocd[20..], commentLength);
        Buffer.BlockCopy(archive, eocdOffset + eocdLength, extended, extendedEocdOffset + eocdLength, commentLength);
        File.WriteAllBytes(archivePath, extended);
    }

    private static string MinimalManifest(string id) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        id,
        displayName = "ZIP Fixture",
        capabilities = new[] { "native-v1" },
        contentHashes = new { }
    });

    private void AssertNoStagingDirectories()
    {
        if (!Directory.Exists(SkinsDirectory)) return;
        Assert.Empty(Directory.GetDirectories(SkinsDirectory, ".music-skin-import-*"));
    }

    public void Dispose()
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bpp-music-skin-import-tests")) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || resolved == parent.TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Unsafe importer fixture cleanup path");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
