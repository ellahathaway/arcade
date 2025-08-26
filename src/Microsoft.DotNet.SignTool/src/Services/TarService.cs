// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Microsoft.DotNet.SignTool.Services;

public interface ITarService
{
#if NETFRAMEWORK
    Task<IEnumerable<ZipDataEntry>> ReadEntriesAsync(string archivePath, string tempDir, bool ignoreContent);
    Task RepackAsync(TaskLoggingHelper log, string tempDir);
    Task<bool> RunProcessAsync(string srcPath, string dstPath);
#else
    IEnumerable<TarEntry> ReadEntries(string path);
    void Repack(TaskLoggingHelper log, string tempDir);
#endif
}

public class TarService() : ITarService
{
#if NETFRAMEWORK
    private readonly IProcessService _processService;
    private readonly string _dotnetPathTooling;
    private readonly string _tarToolPath;

    public TarService(IProcessService processService, string dotnetPathTooling, string tarToolPath)
    {
        _processService = processService;
        _dotnetPathTooling = dotnetPathTooling;
        _tarToolPath = tarToolPath;
    }

    public static async Task<IEnumerable<ZipDataEntry>> ReadEntriesAsync(string archivePath, string tempDir, bool ignoreContent)
    {
        var extractDir = Path.Combine(tempDir, Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(extractDir);

            if (!await RunProcessAsync(archivePath, extractDir))
            {
                throw new Exception($"Failed to unpack tar archive: {archivePath}");
            }

            foreach (var path in Directory.EnumerateFiles(extractDir, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = path.Substring(extractDir.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                using var stream = ignoreContent  ? null : (Stream)File.Open(path, FileMode.Open);
                yield return new ZipDataEntry(relativePath, stream);
            }
        }
        finally
        {
            Directory.Delete(extractDir, recursive: true);
        }
    }

    public async Task RepackAsync(TaskLoggingHelper log, string tempDir)
    {
        var extractDir = Path.Combine(tempDir, Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(extractDir);

            if (!await RunProcessAsync(srcPath: FileSignInfo.FullPath, dstPath: extractDir))
            {
                log.LogMessage(MessageImportance.Low, $"Failed to unpack tar archive: {FileSignInfo.FullPath}");
                return;
            }

            foreach (var path in Directory.EnumerateFiles(extractDir, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = path.Substring(extractDir.Length + 1).Replace(Path.DirectorySeparatorChar, '/');

                var signedPart = FindNestedPart(relativePath);
                if (!signedPart.HasValue)
                {
                    log.LogMessage(MessageImportance.Low, $"Didn't find signed part for nested file: {FileSignInfo.FullPath} -> {relativePath}");
                    continue;
                }

                log.LogMessage(MessageImportance.Low, $"Copying signed stream from {signedPart.Value.FileSignInfo.FullPath} to {FileSignInfo.FullPath} -> {relativePath}.");
                File.Copy(signedPart.Value.FileSignInfo.FullPath, path, overwrite: true);
            }

            if (!await RunProcessAsync(srcPath: extractDir, dstPath: FileSignInfo.FullPath))
            {
                log.LogMessage(MessageImportance.Low, $"Failed to pack tar archive: {FileSignInfo.FullPath}");
                return;
            }
        }
        finally
        {
            Directory.Delete(extractDir, recursive: true);
        }
    }

    private async Task<bool> RunProcessAsync(string srcPath, string dstPath)
    {
        ProcessResult result = await _processService.RunProcessAsync(_dotnetPathTooling, $@"exec ""{_tarToolPath}"" ""{srcPath}"" ""{dstPath}""");
        return result.ExitCode == 0;
    }
#else
    public TarService() { }

    public static IEnumerable<TarEntry> ReadEntries(string path)
    {
        using FileStream streamToDecompress = File.OpenRead(path);
        using GZipStream decompressor = new(streamToDecompress, CompressionMode.Decompress);
        using TarReader tarReader = new(decompressor);
        while (tarReader.GetNextEntry() is TarEntry entry)
        {
            yield return entry;
        }
    }

    public void Repack(TaskLoggingHelper log, string tempDir)
    {
        using MemoryStream streamToCompress = new();
        using (TarWriter writer = new(streamToCompress, leaveOpen: true))
        {
            foreach (TarEntry entry in ReadTarGZipEntries(FileSignInfo.FullPath))
            {
                if (entry.DataStream != null)
                {
                    string relativeName = entry.Name;
                    ZipPart? signedPart = FindNestedPart(relativeName);

                    if (signedPart.HasValue)
                    {
                        using FileStream signedStream = File.OpenRead(signedPart.Value.FileSignInfo.FullPath);
                        entry.DataStream = signedStream;
                        entry.DataStream.Position = 0;
                        writer.WriteEntry(entry);

                        log.LogMessage(MessageImportance.Low, $"Copying signed stream from {signedPart.Value.FileSignInfo.FullPath} to {FileSignInfo.FullPath} -> {relativeName}.");
                        continue;
                    }

                    log.LogMessage(MessageImportance.Low, $"Didn't find signed part for nested file: {FileSignInfo.FullPath} -> {relativeName}");
                }

                writer.WriteEntry(entry);
            }
        }

        streamToCompress.Position = 0;
        using (FileStream outputStream = File.Open(FileSignInfo.FullPath, FileMode.Truncate, FileAccess.Write))
        {
            using GZipStream compressor = new(outputStream, CompressionMode.Compress);
            streamToCompress.CopyTo(compressor);
        }
    }
#endif
}
