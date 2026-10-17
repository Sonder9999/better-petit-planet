using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BetterPetitPlanet.GameTask.Music.Skins;

public sealed record MusicSkinPackageImportResult(string Id, string DisplayName, string? CleanupWarning = null);

/// <summary>Safely stages, validates, and atomically installs a data-only skin package from a local ZIP file.</summary>
public sealed class MusicSkinPackageImporter
{
    private const string StagingDirectoryPrefix = ".music-skin-import-";
    private const long ArchiveMetadataAllowance = 16L * 1024 * 1024;
    private readonly MusicSkinRepository _repository;
    private readonly MusicSkinPackageValidator _validator;
    private readonly MusicSkinResourceLimits _limits;

    public MusicSkinPackageImporter(MusicSkinRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _validator = repository.PackageValidator;
        _limits = _validator.Limits;
    }

    public async Task<MusicSkinPackageImportResult> ImportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) throw new ArgumentException("请选择本地 ZIP 皮肤包", nameof(archivePath));
        if (!Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只支持本地 .zip 皮肤包");
        if (!File.Exists(archivePath)) throw new FileNotFoundException("找不到所选 ZIP 皮肤包", archivePath);
        cancellationToken.ThrowIfCancellationRequested();

        string skinsDirectory = _repository.SkinsDirectory;
        Directory.CreateDirectory(skinsDirectory);
        MusicSkinPackageValidator.RejectReparsePoints(skinsDirectory);
        string? stagingDirectory = null;
        bool installed = false;
        Exception? importFailure = null;
        try
        {
            using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long maximumArchiveBytes = _limits.MaxPackageBytes > long.MaxValue - ArchiveMetadataAllowance
                ? long.MaxValue
                : _limits.MaxPackageBytes + ArchiveMetadataAllowance;
            if (archiveStream.Length > maximumArchiveBytes)
                throw new InvalidOperationException($"ZIP 文件超过 {_limits.MaxPackageBytes} 字节内容上限及 16 MiB 元数据余量");
            ValidateArchiveMetadata(archiveStream, 2L * _limits.MaxFiles);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            List<ArchiveEntryPlan> entries = PlanEntries(archive, cancellationToken);
            string packageRelativeRoot = FindPackageRoot(entries);

            stagingDirectory = Path.Combine(skinsDirectory, StagingDirectoryPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            MusicSkinPackageValidator.RejectReparsePoints(stagingDirectory);
            string extractionDirectory = Path.Combine(stagingDirectory, ".unpacked");
            Directory.CreateDirectory(extractionDirectory);

            long totalBytes = 0;
            byte[] buffer = new byte[64 * 1024];
            foreach (ArchiveEntryPlan plan in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destinationPath = GetDestinationPath(extractionDirectory, plan.RelativePath);
                if (plan.IsDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    MusicSkinPackageValidator.RejectReparsePoints(destinationPath);
                    continue;
                }

                string? parent = Path.GetDirectoryName(destinationPath);
                if (parent == null) throw new InvalidOperationException("ZIP 内文件路径无效");
                Directory.CreateDirectory(parent);
                MusicSkinPackageValidator.RejectReparsePoints(parent);
                string validatorPath = MusicSkinPackageValidator.ResolvePath(extractionDirectory, plan.RelativePath);
                long perFileLimit = GetPerFileLimit(plan.RelativePath);
                long fileBytes = 0;
                using Stream source = plan.Entry.Open();
                await using var destination = new FileStream(validatorPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (read > perFileLimit - fileBytes)
                        throw new InvalidOperationException($"ZIP 内文件超过解压限制：{plan.RelativePath}");
                    if (read > _limits.MaxPackageBytes - totalBytes)
                        throw new InvalidOperationException($"ZIP 解压总字节超过 {_limits.MaxPackageBytes} 上限");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    fileBytes += read;
                    totalBytes += read;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            string packageDirectory = packageRelativeRoot.Length == 0
                ? extractionDirectory
                : Path.Combine(extractionDirectory, packageRelativeRoot.Replace('/', Path.DirectorySeparatorChar));
            string id = ReadManifestId(packageDirectory);
            MusicSkinPackageValidator.ValidateId(id);
            if (packageRelativeRoot.Length == 0)
            {
                string idDirectory = Path.Combine(stagingDirectory, id);
                Directory.Move(packageDirectory, idDirectory);
                packageDirectory = idDirectory;
            }
            if (FindInstalledDirectory(id) != null)
                throw new InvalidOperationException($"皮肤“{id}”已安装，导入已拒绝；请先移除现有皮肤后再试");

            MusicSkinManifest manifest = _validator.ValidateDirectory(packageDirectory, id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string destinationDirectory = Path.Combine(skinsDirectory, manifest.Id);
            if (FindInstalledDirectory(manifest.Id) != null)
                throw new InvalidOperationException($"皮肤“{manifest.Id}”已安装，导入已拒绝；请先移除现有皮肤后再试");

            try
            {
                Directory.Move(packageDirectory, destinationDirectory);
                installed = true;
            }
            catch (IOException ex) when (FindInstalledDirectory(manifest.Id) != null)
            {
                throw new InvalidOperationException($"皮肤“{manifest.Id}”已安装，导入已拒绝；未覆盖现有皮肤", ex);
            }

            var result = new MusicSkinPackageImportResult(manifest.Id, manifest.DisplayName);
            try
            {
                Directory.Delete(stagingDirectory, recursive: true);
                stagingDirectory = null;
            }
            catch (Exception ex) when (installed)
            {
                string leftoverDirectory = stagingDirectory!;
                stagingDirectory = null;
                return result with { CleanupWarning = $"皮肤已安装，但临时目录清理失败：{leftoverDirectory}（{ex.Message}）" };
            }
            return result;
        }
        catch (OperationCanceledException ex)
        {
            importFailure = ex;
            throw;
        }
        catch (InvalidOperationException ex)
        {
            importFailure = ex;
            throw;
        }
        catch (InvalidDataException ex)
        {
            importFailure = new InvalidOperationException("ZIP 文件损坏或格式不受支持", ex);
            throw importFailure;
        }
        catch (JsonException ex)
        {
            importFailure = new InvalidOperationException("皮肤清单 JSON 无效", ex);
            throw importFailure;
        }
        catch (ArgumentException ex)
        {
            importFailure = new InvalidOperationException("皮肤包路径或清单字段无效：" + ex.Message, ex);
            throw importFailure;
        }
        catch (Exception ex)
        {
            importFailure = ex;
            throw;
        }
        finally
        {
            if (stagingDirectory != null && Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (Exception cleanupError) when (importFailure != null)
                {
                    throw new InvalidOperationException("导入失败，且暂存目录清理失败：" + stagingDirectory,
                        new AggregateException(importFailure, cleanupError));
                }
            }
        }
    }

    private List<ArchiveEntryPlan> PlanEntries(ZipArchive archive, CancellationToken cancellationToken)
    {
        long maximumEntries = 2L * _limits.MaxFiles;
        if (archive.Entries.Count > maximumEntries)
            throw new InvalidOperationException($"ZIP 条目数量超过 {_limits.MaxFiles} 个文件和 {_limits.MaxFiles} 个目录的上限");

        var plans = new List<ArchiveEntryPlan>(archive.Entries.Count);
        var explicitEntries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectSpecialEntry(entry);
            (string relativePath, bool isDirectory) = NormalizeEntryPath(entry);
            if (!isDirectory && !IsAllowedDataFile(relativePath))
                throw new InvalidOperationException($"ZIP 只允许 JSON/PNG/JPG/JPEG 数据文件：{relativePath}");
            if (!explicitEntries.TryAdd(relativePath, isDirectory))
                throw new InvalidOperationException($"ZIP 包含重复路径（大小写不敏感）：{relativePath}");

            string[] segments = relativePath.Split('/');
            int directoryDepth = isDirectory ? segments.Length : segments.Length - 1;
            if (directoryDepth > _limits.MaxFiles)
                throw new InvalidOperationException($"ZIP 路径深度超过 {_limits.MaxFiles} 个目录上限：{relativePath}");

            for (int i = 1; i < segments.Length; i++)
            {
                string parent = string.Join("/", segments.Take(i));
                if (explicitEntries.TryGetValue(parent, out bool parentIsDirectory) && !parentIsDirectory)
                    throw new InvalidOperationException($"ZIP 路径同时被用作文件和目录：{parent}");
                if (directories.Add(parent) && directories.Count > _limits.MaxFiles)
                    throw new InvalidOperationException($"ZIP 目录数量超过 {_limits.MaxFiles} 上限");
            }

            if (isDirectory)
            {
                if (directories.Add(relativePath) && directories.Count > _limits.MaxFiles)
                    throw new InvalidOperationException($"ZIP 目录数量超过 {_limits.MaxFiles} 上限");
            }
            else
            {
                fileCount++;
                if (fileCount > _limits.MaxFiles)
                    throw new InvalidOperationException($"ZIP 文件数量超过 {_limits.MaxFiles} 上限");
                if (explicitEntries.Keys.Any(path => path.Length > relativePath.Length &&
                    path.StartsWith(relativePath + "/", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"ZIP 路径同时被用作文件和目录：{relativePath}");
            }

            plans.Add(new ArchiveEntryPlan(entry, relativePath, isDirectory));
        }

        if (fileCount == 0) throw new InvalidOperationException("ZIP 皮肤包没有数据文件");
        return plans;
    }

    private static void ValidateArchiveMetadata(FileStream archiveStream, long maximumEntries)
    {
        const int eocdLength = 22;
        const uint eocdSignature = 0x06054b50;
        const uint zip64EocdSignature = 0x06064b50;
        const uint zip64LocatorSignature = 0x07064b50;
        const ushort zip64UShortSentinel = ushort.MaxValue;
        const uint zip64UIntSentinel = uint.MaxValue;

        long fileLength = archiveStream.Length;
        int tailLength = (int)Math.Min(fileLength, eocdLength + ushort.MaxValue);
        if (tailLength < eocdLength) throw new InvalidDataException("ZIP 结束目录记录缺失");
        byte[] tail = new byte[tailLength];
        archiveStream.Position = fileLength - tailLength;
        archiveStream.ReadExactly(tail);

        int eocdOffsetInTail = -1;
        int matchingEocdCount = 0;
        for (int offset = tail.Length - eocdLength; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset, sizeof(uint))) == eocdSignature &&
                offset + eocdLength + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20, sizeof(ushort))) == tail.Length)
            {
                eocdOffsetInTail = offset;
                matchingEocdCount++;
            }
        }
        if (eocdOffsetInTail < 0) throw new InvalidDataException("ZIP 结束目录记录缺失或注释长度无效");
        if (matchingEocdCount != 1)
            throw new InvalidOperationException("ZIP 结束目录签名存在歧义（可能位于 ZIP 注释中）");

        long eocdAbsoluteOffset = fileLength - tailLength + eocdOffsetInTail;
        ReadOnlySpan<byte> eocd = tail.AsSpan(eocdOffsetInTail, eocdLength);
        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]);
        ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]);
        ushort entriesOnDisk16 = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..]);
        ushort totalEntries16 = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        uint centralDirectorySize32 = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        uint centralDirectoryOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        if (diskNumber != 0 || centralDirectoryDisk != 0)
            throw new InvalidOperationException("不支持跨多个磁盘分卷的 ZIP 皮肤包");

        ulong totalEntries;
        ulong centralDirectorySize;
        ulong centralDirectoryOffset;
        long centralDirectoryEnd;
        bool requiresZip64 = entriesOnDisk16 == zip64UShortSentinel || totalEntries16 == zip64UShortSentinel ||
            centralDirectorySize32 == zip64UIntSentinel || centralDirectoryOffset32 == zip64UIntSentinel;
        long locatorOffset = eocdAbsoluteOffset - 20;
        bool hasZip64Locator = false;
        byte[] locator = new byte[20];
        if (locatorOffset >= 0)
        {
            archiveStream.Position = locatorOffset;
            archiveStream.ReadExactly(locator);
            hasZip64Locator = BinaryPrimitives.ReadUInt32LittleEndian(locator) == zip64LocatorSignature;
        }

        if (requiresZip64 && !hasZip64Locator)
            throw new InvalidDataException("ZIP64 结束目录缺失或不完整");
        if (requiresZip64)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                throw new InvalidOperationException("不支持跨多个磁盘分卷的 ZIP64 皮肤包");

            ulong zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (zip64Offset > (ulong)locatorOffset || (ulong)locatorOffset - zip64Offset < 56UL)
                throw new InvalidDataException("ZIP64 结束目录位置无效");
            byte[] zip64Record = new byte[56];
            archiveStream.Position = (long)zip64Offset;
            archiveStream.ReadExactly(zip64Record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64Record) != zip64EocdSignature)
                throw new InvalidDataException("ZIP64 结束目录记录无效");

            ulong recordSize = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(4));
            if (recordSize < 44 || recordSize > (ulong)ArchiveMetadataAllowance ||
                recordSize > (ulong)locatorOffset - zip64Offset - 12UL)
                throw new InvalidOperationException("ZIP64 结束目录元数据超过允许范围");

            uint zip64Disk = BinaryPrimitives.ReadUInt32LittleEndian(zip64Record.AsSpan(16));
            uint zip64CentralDirectoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(zip64Record.AsSpan(20));
            ulong entriesOnDisk64 = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(24));
            totalEntries = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(32));
            centralDirectorySize = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(40));
            centralDirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(zip64Record.AsSpan(48));
            if (zip64Disk != 0 || zip64CentralDirectoryDisk != 0 || entriesOnDisk64 != totalEntries)
                throw new InvalidOperationException("不支持跨多个磁盘分卷的 ZIP64 皮肤包");
            if ((entriesOnDisk16 != zip64UShortSentinel && entriesOnDisk16 != entriesOnDisk64) ||
                (totalEntries16 != zip64UShortSentinel && totalEntries16 != totalEntries) ||
                (centralDirectorySize32 != zip64UIntSentinel && centralDirectorySize32 != centralDirectorySize) ||
                (centralDirectoryOffset32 != zip64UIntSentinel && centralDirectoryOffset32 != centralDirectoryOffset))
                throw new InvalidDataException("ZIP 与 ZIP64 结束目录数据不一致");
            centralDirectoryEnd = (long)zip64Offset;
        }
        else
        {
            if (entriesOnDisk16 != totalEntries16)
                throw new InvalidDataException("ZIP 中央目录条目计数不一致");
            totalEntries = totalEntries16;
            centralDirectorySize = centralDirectorySize32;
            centralDirectoryOffset = centralDirectoryOffset32;
            centralDirectoryEnd = eocdAbsoluteOffset;
        }

        if (totalEntries > (ulong)maximumEntries)
            throw new InvalidOperationException($"ZIP 条目数量超过 {maximumEntries} 上限");
        if (centralDirectorySize > (ulong)ArchiveMetadataAllowance)
            throw new InvalidOperationException("ZIP 中央目录超过 16 MiB 元数据上限");
        if (centralDirectorySize > (ulong)centralDirectoryEnd || centralDirectoryOffset > (ulong)centralDirectoryEnd ||
            centralDirectorySize > (ulong)centralDirectoryEnd - centralDirectoryOffset)
            throw new InvalidDataException("ZIP 中央目录边界无效");
        if (centralDirectoryOffset + centralDirectorySize != (ulong)centralDirectoryEnd)
            throw new InvalidOperationException("ZIP 中央目录偏移与大小和结束目录位置不一致");
    }

    private static (string RelativePath, bool IsDirectory) NormalizeEntryPath(ZipArchiveEntry entry)
    {
        string fullName = entry.FullName;
        if (string.IsNullOrWhiteSpace(fullName) || Path.IsPathRooted(fullName) || fullName.StartsWith('/') ||
            fullName.StartsWith('\\') || fullName.Contains(':'))
            throw new InvalidOperationException($"ZIP 包含绝对路径或盘符路径：{fullName}");

        string normalized = fullName.Replace('\\', '/');
        bool isDirectory = normalized.EndsWith('/') ||
            (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0;
        if (normalized.EndsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException($"ZIP 路径包含重复分隔符：{fullName}");
        if (normalized.EndsWith('/')) normalized = normalized[..^1];
        if (normalized.Length == 0)
            throw new InvalidOperationException("ZIP 根路径条目无效");

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Contains("..", StringComparison.Ordinal) ||
                segment.EndsWith('.') || segment.EndsWith(' ') || segment.Any(char.IsControl) ||
                segment.IndexOfAny([ '<', '>', '"', '|', '?', '*' ]) >= 0 ||
                Regex.IsMatch(segment.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])\z", RegexOptions.IgnoreCase))
                throw new InvalidOperationException($"ZIP 包含不安全路径：{fullName}");
        }

        return (string.Join("/", segments), isDirectory);
    }

    private static void RejectSpecialEntry(ZipArchiveEntry entry)
    {
        int attributes = entry.ExternalAttributes;
        int unixType = (attributes >> 16) & 0xF000;
        if ((attributes & (int)FileAttributes.ReparsePoint) != 0 || unixType == 0xA000)
            throw new InvalidOperationException($"ZIP 不允许符号链接或重解析点：{entry.FullName}");
        if (unixType != 0 && unixType is not (0x8000 or 0x4000))
            throw new InvalidOperationException($"ZIP 不允许特殊文件类型：{entry.FullName}");
    }

    private static bool IsAllowedDataFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".json" or ".png" or ".jpg" or ".jpeg";

    private long GetPerFileLimit(string path) => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
        ? _limits.MaxManifestBytes
        : _limits.MaxImageBytes;

    private string GetDestinationPath(string extractionDirectory, string relativePath)
    {
        string root = Path.GetFullPath(extractionDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ZIP 路径超出暂存目录：{relativePath}");
        return destination;
    }

    private static string FindPackageRoot(IReadOnlyList<ArchiveEntryPlan> entries)
    {
        if (entries.Any(entry => !entry.IsDirectory && entry.RelativePath.Equals("skin.json", StringComparison.OrdinalIgnoreCase)))
            return string.Empty;

        string[] roots = entries.Select(entry => entry.RelativePath.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length != 1 || !entries.Any(entry => !entry.IsDirectory &&
            entry.RelativePath.Equals(roots[0] + "/skin.json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("ZIP 根目录格式无效：仅接受根 skin.json 或唯一 <id>/skin.json 包装目录");
        return roots[0];
    }

    private string ReadManifestId(string packageDirectory)
    {
        string manifestPath = Path.Combine(packageDirectory, "skin.json");
        using FileStream stream = MusicSkinPackageValidator.OpenRead(manifestPath);
        if (stream.Length > _limits.MaxManifestBytes)
            throw new InvalidOperationException($"skin.json 超过 {_limits.MaxManifestBytes} 字节上限");
        using JsonDocument manifest = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        if (!manifest.RootElement.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
            throw new InvalidOperationException("skin.json 缺少有效 id");
        return id.GetString()!;
    }

    private string? FindInstalledDirectory(string id)
    {
        if (!Directory.Exists(_repository.SkinsDirectory)) return null;
        return Directory.EnumerateDirectories(_repository.SkinsDirectory)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ArchiveEntryPlan(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
}
