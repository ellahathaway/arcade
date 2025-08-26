// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Microsoft.DotNet.SignTool.Services;

public interface IPkgService
{
    Task<IEnumerable<ZipDataEntry>> ReadEntriesAsync(string archivePath, string tempDir, bool ignoreContent);
    Task RepackAsync(TaskLoggingHelper log, string tempDir);
    Task<bool> RunProcessAsync(string srcPath, string dstPath, string action);
}

public class PkgService() : IPkgService
{
    private readonly IProcessService _processService;
    private readonly string _dotnetPathTooling;
    private readonly string _pkgToolPath;

    public PkgService(IProcessService processService, string dotnetPathTooling, string pkgToolPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new Exception($"Pkg tooling is only supported on MacOS.");
        }

        _processService = processService;
        _dotnetPathTooling = dotnetPathTooling;
        _pkgToolPath = pkgToolPath;
    }

    public async Task<IEnumerable<ZipDataEntry>> ReadEntriesAsync(string archivePath, string tempDir, bool ignoreContent)
    {
        string extractDir = Path.Combine(tempDir, Guid.NewGuid().ToString());
        try
        {
            if (!await RunProcessAsync(archivePath, extractDir, "unpack"))
            {
                throw new Exception($"Failed to unpack pkg {archivePath}");
            }

            foreach (var path in Directory.EnumerateFiles(extractDir, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = path.Substring(extractDir.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                using var stream = ignoreContent ? null : (Stream)File.Open(path, FileMode.Open);
                yield return new ZipDataEntry(relativePath, stream);
            }
        }
        finally
        {
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }
        }
    }

    private async Task RepackAsync(TaskLoggingHelper log, string tempDir)
    {
        string extractDir = Path.Combine(tempDir, Guid.NewGuid().ToString());
        try
        {
            if (!await RunProcessAsync(srcPath: FileSignInfo.FullPath, dstPath: extractDir, "unpack"))
            {
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

            if (!await RunProcessAsync(srcPath: extractDir, dstPath: FileSignInfo.FullPath, "pack"))
            {
                return;
            }
        }
        finally
        {
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }
        }
    }

    private async Task<bool> RunProcessAsync(string srcPath, string dstPath, string action)
    {
        string args = $@"{action} ""{srcPath}""";

        if (action != "verify")
        {
            args += $@" ""{dstPath}""";
        }

        ProcessResult result = await _processService.RunProcessAsync(_dotnetPathTooling, $@"exec ""{_pkgToolPath}"" {args}");
        return result.ExitCode == 0;
    }
}
