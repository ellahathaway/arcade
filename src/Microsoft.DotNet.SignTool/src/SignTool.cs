// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.SignTool
{
    internal abstract class SignTool
    {
        private readonly SignToolArgs _args;
        internal readonly TaskLoggingHelper _log;
        internal string TempDir => _args.TempDir;
        internal string MicroBuildCorePath => _args.MicroBuildCorePath;

        internal string WixToolsPath => _args.WixToolsPath;
        internal string TarToolPath => _args.TarToolPath;
        internal string PkgToolPath => _args.PkgToolPath;

        internal SignTool(SignToolArgs args, TaskLoggingHelper log)
        {
            _args = args;
            _log = log;
        }

        public abstract void RemovePublicSign(string assemblyPath);

        public abstract bool LocalStrongNameSign(IBuildEngine buildEngine, int round, IEnumerable<FileSignInfo> files);

        public abstract bool VerifySignedPEFile(Stream stream);
        public abstract bool VerifySignedPowerShellFile(string filePath);
        public abstract bool VerifySignedNugetFileMarker(string filePath);
        public abstract bool VerifySignedVSIXFileMarker(string filePath);
        public abstract bool VerifySignedPkgFile(string filePath, string pkgToolPath);

        public abstract bool VerifyStrongNameSign(string fileFullPath);

        public abstract bool RunMSBuild(IBuildEngine buildEngine, string projectFilePath, string binLogPath);

        public bool Sign(IBuildEngine buildEngine, int round, IEnumerable<FileSignInfo> files)
        {
            return LocalStrongNameSign(buildEngine, round, files)
                && AuthenticodeSign(buildEngine, round, files);
        }

        private bool AuthenticodeSign(IBuildEngine buildEngine, int round, IEnumerable<FileSignInfo> filesToSign)
        {
            var signingDir = Path.Combine(_args.TempDir, "Signing");
            var nonOSXFilesToSign = filesToSign.Where(fsi => !SignToolConstants.SignableOSXExtensions.Contains(Path.GetExtension(fsi.FileName)));
            var osxFilesToSign = filesToSign.Where(fsi => SignToolConstants.SignableOSXExtensions.Contains(Path.GetExtension(fsi.FileName)));

            var nonOSXSigningStatus = true;
            var osxSigningStatus = true;

            Directory.CreateDirectory(signingDir);

            if (nonOSXFilesToSign.Any())
            {
                var nonOSXBuildFilePath = Path.Combine(signingDir, $"Round{round}.proj");
                var nonOSXProjContent = GenerateBuildFileContent(nonOSXFilesToSign);

                File.WriteAllText(nonOSXBuildFilePath, nonOSXProjContent);
                nonOSXSigningStatus = RunMSBuild(buildEngine, nonOSXBuildFilePath, Path.Combine(_args.LogDir, $"Signing{round}.binlog"));
            }

            if (osxFilesToSign.Any())
                {
                    var filesGroupedByCertificate = osxFilesToSign.GroupBy(fsi => fsi.SignInfo.Certificate);

                    foreach (var osxFileGroup in filesGroupedByCertificate)
                    {
                        var certificate = osxFileGroup.Key;
                        var osxBuildFilePath = Path.Combine(signingDir, $"Round{round}-OSX-Cert{certificate}.proj");

                        string osxFilesZippingDir = Path.Combine(signingDir, osxFileGroup.Key);
                        Directory.CreateDirectory(osxFilesZippingDir);

                        // Zip the files
                        foreach (FileSignInfo item in osxFileGroup)
                        {
                            string zipFilePath = GetZipFilePath(osxFilesZippingDir, item.FileName);
                            using (var zip = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
                            {
                                zip.CreateEntryFromFile(item.FullPath, item.FileName);
                            }
                        }

                        var osxProjContent = GenerateBuildFileContent(osxFileGroup, osxFilesZippingDir, isOSX: true);

                        File.WriteAllText(osxBuildFilePath, osxProjContent);

                        osxSigningStatus = RunMSBuild(buildEngine, osxBuildFilePath, Path.Combine(_args.LogDir, $"Signing{round}-OSX.binlog"));

                        // Unzip the files and copy them back to their original locations
                        foreach (var item in osxFileGroup)
                        {
                            string zipFilePath = GetZipFilePath(osxFilesZippingDir, item.FileName);
                            using (var zip = ZipFile.OpenRead(zipFilePath))
                            {
                                zip.Entries.First().ExtractToFile(item.FullPath, overwrite: true);
                            }
                        }

                        Directory.Delete(osxFilesZippingDir, recursive: true);
                    }
                }

            return nonOSXSigningStatus && osxSigningStatus;
        }

        private string GenerateBuildFileContent(IEnumerable<FileSignInfo> filesToSign, string zipFileDir = "", bool isOSX = false)
        {
            if (isOSX && string.IsNullOrEmpty(zipFileDir))
            {
                throw new ArgumentException("zipFileDir must be specified when signing OSX files.");
            }

            var builder = new StringBuilder();
            AppendLine(builder, depth: 0, text: @"<?xml version=""1.0"" encoding=""utf-8""?>");
            AppendLine(builder, depth: 0, text: @"<Project DefaultTargets=""AfterBuild"">");

            // Setup the code to get the NuGet package root.
            var signKind = _args.TestSign ? "test" : "real";
            AppendLine(builder, depth: 1, text: @"<PropertyGroup>");
            if (isOSX)
            {
                AppendLine(builder, depth: 2, text: @"<EnableCodeSigning>false</EnableCodeSigning>");
            }
            AppendLine(builder, depth: 2, text: $@"<OutDir>{_args.EnclosingDir}</OutDir>");
            AppendLine(builder, depth: 2, text: $@"<IntermediateOutputPath>{_args.TempDir}</IntermediateOutputPath>");
            AppendLine(builder, depth: 2, text: $@"<SignType>{signKind}</SignType>");
            AppendLine(builder, depth: 1, text: @"</PropertyGroup>");

            AppendLine(builder, depth: 1, text: $@"<Import Project=""{Path.Combine(MicroBuildCorePath, "build", "MicroBuild.Core.props")}"" />");

            AppendLine(builder, depth: 1, text: $@"<ItemGroup>");

            foreach (var fileToSign in filesToSign)
            {
                string filePath = isOSX ? GetZipFilePath(zipFileDir, fileToSign.FileName) : fileToSign.FullPath;
                AppendLine(builder, depth: 2, text: $@"<FilesToSign Include=""{Uri.EscapeDataString(filePath)}"">");
                AppendLine(builder, depth: 3, text: $@"<Authenticode>{fileToSign.SignInfo.Certificate}</Authenticode>");
                if (fileToSign.SignInfo.StrongName != null && !fileToSign.SignInfo.ShouldLocallyStrongNameSign)
                {
                    AppendLine(builder, depth: 3, text: $@"<StrongName>{fileToSign.SignInfo.StrongName}</StrongName>");
                }
                if(isOSX)
                {
                    AppendLine(builder, depth: 3, text: $@"<Zip>true</Zip>");
                }
                AppendLine(builder, depth: 2, text: @"</FilesToSign>");
            }

            AppendLine(builder, depth: 1, text: $@"</ItemGroup>");

            // The MicroBuild targets hook AfterBuild to do the signing hence we just make it our no-op default target
            AppendLine(builder, depth: 1, text: @"<Target Name=""AfterBuild"">");
            AppendLine(builder, depth: 2, text: @"<Message Text=""Running signing process."" />");
            AppendLine(builder, depth: 1, text: @"</Target>");

            AppendLine(builder, depth: 1, text: $@"<Import Project=""{Path.Combine(MicroBuildCorePath, "build", "MicroBuild.Core.targets")}"" />");
            AppendLine(builder, depth: 0, text: @"</Project>");

            return builder.ToString();
        }

        private static string GetZipFilePath(string zipFileDir, string fileName) =>
            Path.Combine(zipFileDir, Path.GetFileNameWithoutExtension(fileName) + ".zip");

        private static void AppendLine(StringBuilder builder, int depth, string text)
        {
            for (int i = 0; i < depth; i++)
            {
                builder.Append("    ");
            }

            builder.AppendLine(text);
        }

        protected bool LocalStrongNameSign(FileSignInfo file)
        {
            if (!File.Exists(_args.SNBinaryPath) || !_args.SNBinaryPath.EndsWith("sn.exe"))
            {
                _log.LogError($"Found file that needs to be strong-name sign ({file.FullPath}), but path to 'sn.exe' wasn't specified.");
                return false;
            }

            _log.LogMessage($"Strong-name signing {file.FullPath} locally.");

            // sn -R <path_to_file> <path_to_snk>
            var process = Process.Start(new ProcessStartInfo()
            {
                FileName = _args.SNBinaryPath,
                Arguments = $@"-R ""{file.FullPath}"" ""{file.SignInfo.StrongName}""",
                UseShellExecute = false,
                WorkingDirectory = TempDir,
            });

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                _log.LogError($"Failed to strong-name sign file {file.FullPath}");
                return false;
            }

            return true;
        }
    }
}
