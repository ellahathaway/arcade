// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

#nullable enable

namespace Microsoft.DotNet.Pkg
{
    public static class Pkg
    {
        internal static string WorkingDirectory = Directory.GetCurrentDirectory();
        internal static string InputPath = string.Empty;
        internal static string OutputPath = string.Empty;

        public static void Unpack(string inputPath, string outputPath)
            => ProcessPkg(inputPath, outputPath, repacking: false);
        
        public static void Repack(string inputPath, string outputPath)
            => ProcessPkg(inputPath, outputPath, repacking: true);

        private static void ProcessPkg(string inputPath, string outputPath, bool repacking)
        {
            if (string.IsNullOrEmpty(inputPath) || string.IsNullOrEmpty(outputPath))
            {
                throw new Exception("Input and output paths must be provided");
            }

            if (!repacking && !IsPkg(inputPath) && !File.Exists(inputPath))
            {
                throw new Exception("Input paths must be a .pkg file");
            }
            
            if (repacking && !Directory.Exists(inputPath))
            {
                throw new Exception("Input path must be a directory");
            }

            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            InputPath = inputPath;
            OutputPath = outputPath;

            UnpackedPkg unpackedPkg = new UnpackedPkg(repacking);

        }

        internal static List<string> GetNestedApplications(string? srcDir) =>
            srcDir == null
                ? new List<string>()
                : GetDirectories(srcDir, "*.app").ToList();

        internal static bool IsPkg(string path) =>
            Path.GetExtension(path).Equals(".pkg");

        internal static string? FindInPath(string name, string path, bool isDirectory, SearchOption searchOption = SearchOption.AllDirectories)
        {
            try
            {
                List<string> results = isDirectory ? GetDirectories(path, name, searchOption).ToList() : GetFiles(path, name, searchOption).ToList();
                if (results.Count == 1)
                {
                    return results[0];
                }
                else if (results.Count > 1)
                {
                    throw new Exception($"Multiple files found with name '{name}'");
                }
                else
                {
                    return null;
                }
            }
            catch (Exception e)
            {
                throw new Exception($"Error finding file '{name}' in '{path}: {e.Message}");
            }
        }

        internal static IEnumerable<string> GetDirectories(string path, string pathFilter = "*", SearchOption searchOption = SearchOption.AllDirectories) =>
            Directory.EnumerateDirectories(path, pathFilter, searchOption);

        internal static IEnumerable<string> GetFiles(string path, string pathFilter = "*", SearchOption searchOption = SearchOption.AllDirectories) =>
            Directory.EnumerateFiles(path, pathFilter, searchOption);
    }

    internal class UnpackedPkg
    {
        private string NameWithExtension;
        private string NameWithoutExtension;
        private string LocalExtractionPath;
        private string? Identifier = null;
        private string? Resources = null;
        private string? Distribution = null;
        private string? Scripts = null;
        private List<UnpackedBundle> Bundles = new List<UnpackedBundle>();
        internal static string BundlesRepackPath = string.Empty;

        internal UnpackedPkg(bool repacking)
        {
            if (repacking)
            {
                NameWithoutExtension = Path.GetFileName(Pkg.InputPath);
                NameWithExtension = NameWithoutExtension + ".pkg";
                LocalExtractionPath = Pkg.InputPath;
                BundlesRepackPath = Path.Combine(Pkg.OutputPath, NameWithoutExtension);
                if (!Directory.Exists(BundlesRepackPath))
                {
                    Directory.CreateDirectory(BundlesRepackPath);
                }
            }
            else
            {
                NameWithExtension = Path.GetFileName(Pkg.InputPath);
                NameWithoutExtension = Path.GetFileNameWithoutExtension(NameWithExtension);
                LocalExtractionPath = Path.Combine(Pkg.OutputPath, NameWithoutExtension);
            }

            if (!repacking)
            {
                ExpandPkg();
            }

            Resources = Pkg.FindInPath("Resources", LocalExtractionPath, isDirectory: true, searchOption: SearchOption.TopDirectoryOnly);
            Distribution = Pkg.FindInPath("Distribution", LocalExtractionPath, isDirectory: false, searchOption: SearchOption.TopDirectoryOnly);
            Scripts = Pkg.FindInPath("Scripts", LocalExtractionPath, isDirectory: true, searchOption: SearchOption.TopDirectoryOnly);

            if (!string.IsNullOrEmpty(Distribution))
            {
                var xml = XElement.Load(Distribution);
                List<XElement> elements = xml.Elements("pkg-ref").Where(e => e.Value.Trim() != "").ToList();
                if (!elements.Any())
                {
                    throw new Exception("No pkg-ref elements found in Distribution file");
                }
                Identifier = GetBundleId(elements[0]);
                foreach (var element in elements)
                {
                    string bundleExtractionPath = Path.Combine(LocalExtractionPath, element.Value.Substring(1));
                    string bundleVersion = element.Attribute("version")?.Value ?? throw new Exception($"No version found in bundle file {NameWithExtension}");
                    Bundles.Add(new UnpackedBundle(bundleExtractionPath, GetBundleId(element), bundleVersion, NameWithExtension, repacking));
                }
            }
            else
            {
                string? packageInfo = Pkg.FindInPath("PackageInfo", LocalExtractionPath, isDirectory: false, searchOption: SearchOption.TopDirectoryOnly);
                if (!string.IsNullOrEmpty(packageInfo))
                {
                    // This is a single bundle package
                    XElement pkgInfo = XElement.Load(packageInfo);
                    Identifier = GetBundleId(pkgInfo);
                    string version = pkgInfo.Attribute("version")?.Value ?? throw new Exception("No version found in PackageInfo file");
                    Bundles.Add(new UnpackedBundle(LocalExtractionPath, NameWithoutExtension, version, NameWithExtension, repacking, isNested: false));
                }
            }

            if (repacking)
            {
                RepackPkg();
                Directory.Delete(BundlesRepackPath, true);
            }
        }

        private void ExpandPkg()
        {
            if (Directory.Exists(LocalExtractionPath))
            {
                Directory.Delete(LocalExtractionPath, true);
            }

            ExecuteHelper.Run("pkgutil", $"--expand {Pkg.InputPath} {LocalExtractionPath}");
        }

        private void RepackPkg()
        {
            if (string.IsNullOrEmpty(Distribution))
            {
                if (Bundles.Count == 1)
                {
                    // This is a single bundle package
                    // We already repacked the bundle, so we just need to move it to the desired output path
                    string outputPackagePath = Path.Combine(Pkg.OutputPath, NameWithExtension);
                    File.Move(Path.Combine(BundlesRepackPath, NameWithExtension), outputPackagePath);
                }
                
                if (Bundles.Count > 1)
                {
                    // This is a multi-bundle package and should contain a Distribution file
                    throw new Exception("No Distribution file found in multi-bundle package");
                }
            }
            else
            {
                string args = string.Empty;
                args += $"--distribution {Distribution}";
                if (Bundles.Any())
                {
                    args += $" --package-path {BundlesRepackPath}";
                }
                if (!string.IsNullOrEmpty(Resources))
                {
                    args += $" --resources {Resources}";
                }
                if (!string.IsNullOrEmpty(Scripts))
                {
                    args += $" --scripts {Scripts}";
                }
                if (args.Length == 0)
                {
                    args += $" --root {LocalExtractionPath}";
                }
                string outputPackagePath = Path.Combine(Pkg.OutputPath, NameWithExtension);
                args += $" {outputPackagePath}";

                ExecuteHelper.Run("productbuild", args);
            }
        }

        private static string GetBundleId(XElement element)
        {
            string bundleId = element.Attribute("packageIdentifier")?.Value
                ?? element.Attribute("id")?.Value
                ?? element.Attribute("identifier")?.Value
                ?? throw new Exception("No packageIdentifier or id found in XElement.");

            return bundleId;
        }

        private class UnpackedBundle
        {
            private string NameWithExtension;
            private string NameWithoutExtension;
            private string LocalExtractionPath;
            private string Identifier;
            private string Version;
            private string? Scripts;
            private string? Payload;
            private string? PayloadDir;

            internal UnpackedBundle(string localExtractionPath, string identifier, string version, string rootPkgName, bool repacking = false, bool isNested = true)
            {
                NameWithExtension = isNested ? Path.GetFileName(localExtractionPath) : rootPkgName;
                NameWithoutExtension = Path.GetFileNameWithoutExtension(NameWithExtension);
                LocalExtractionPath = isNested ? Path.Combine(Path.GetDirectoryName(localExtractionPath) ?? string.Empty, NameWithoutExtension) : localExtractionPath;
                Identifier = identifier;
                Version = version;


                if (!Pkg.IsPkg(NameWithExtension))
                {
                    throw new Exception($"Bundle '{NameWithExtension}' is not a .pkg file");
                }

                if (!repacking && isNested)
                {
                    // The nested bundles get unpacked into a directory with a .pkg extension,
                    // so we remove this extension when unpacking the bundle
                    Directory.Move(localExtractionPath, LocalExtractionPath);
                }

                Scripts = Pkg.FindInPath("Scripts", LocalExtractionPath, isDirectory: true, searchOption: SearchOption.TopDirectoryOnly);
                Payload = Pkg.FindInPath("Payload", LocalExtractionPath, isDirectory: repacking, searchOption: SearchOption.TopDirectoryOnly);

                if (!string.IsNullOrEmpty(Payload))
                {
                    PayloadDir = Payload; // We replace the payload file with the payload directory when we unpack it
                    if(!repacking)
                    {
                        UnpackPayloadFile(Path.GetFullPath(Payload));
                    }

                    if (repacking)
                    {
                        PkgBuild();
                    }
                }
            }

            private void PkgBuild()
            {
                string info = GenerateInfoPlist();
                string args = $"--root {PayloadDir} --component-plist {info} --identifier {Identifier} --version {Version} --keychain login.keychain --install-location /usr/local/share/dotnet";
                if (!string.IsNullOrEmpty(Scripts))
                {
                    args += $" --scripts {Scripts}";
                }
                string outputPackagePath = Path.Combine(UnpackedPkg.BundlesRepackPath, NameWithExtension);
                args += $" {outputPackagePath}";

                ExecuteHelper.Run("pkgbuild", args);

                File.Delete(info);
            }

            private string GenerateInfoPlist()
            {
                string info = Path.Combine(Pkg.WorkingDirectory, "Info.plist");
                ExecuteHelper.Run("pkgbuild", $"--analyze --root {LocalExtractionPath} {info}");
                return info;
            }

            private void UnpackPayloadFile(string payloadFilePath)
            {
                if (!File.Exists(payloadFilePath) || !Path.GetFileName(payloadFilePath).Equals("Payload"))
                {
                    throw new Exception($"Cannot unpack invalid 'Payload' file in {NameWithExtension}");
                }

                string tempDir = Path.Combine(Pkg.WorkingDirectory, "tempPayloadUnpackingDir");
                Directory.CreateDirectory(tempDir);
                try
                {
                    Directory.SetCurrentDirectory(tempDir);

                    // While we're shelling out to an executable named 'tar', the "Payload" file from pkgs is not actually
                    // a tar file.  It's secretly a 'pbzx' file that tar on OSX has been taught to unpack.
                    // As such, while there is actually untarring / re-tarring in this file using Python libraries, we have to
                    // shell out to the host machine to do this.
                    ExecuteHelper.Run("tar", $"-xf {payloadFilePath}");
                }
                finally
                {
                    Directory.SetCurrentDirectory(Pkg.WorkingDirectory);

                    // Remove the payload file and replace it with a directory of the same name containing the unpacked contents
                    File.Delete(payloadFilePath);
                    Directory.Move(tempDir, payloadFilePath);
                }
            }
        }
    }
}
