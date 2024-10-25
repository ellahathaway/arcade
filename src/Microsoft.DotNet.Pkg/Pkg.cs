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
            => Process(inputPath, outputPath, repacking: false);
        
        public static void Repack(string inputPath, string outputPath)
            => Process(inputPath, outputPath, repacking: true);

        public static void VerifySignature(string inputPath)
        {
            if (string.IsNullOrEmpty(inputPath))
            {
                throw new Exception("Input path must be provided");
            }

            if (!IsPkg(inputPath))
            {
                throw new Exception("Input path must be a .pkg file");
            }

            string full_path = Path.GetFullPath(inputPath);
            string output = ExecuteHelper.Run("pkgutil", $"--check-signature {full_path}");
            if (output.Contains("Status: no signature"))
            {
                throw new Exception("No signature found in package");
            }
        }

        private static void Process(string inputPath, string outputPath, bool repacking)
        {
            InputPath = inputPath;
            OutputPath = outputPath;

            if (string.IsNullOrEmpty(InputPath) || string.IsNullOrEmpty(OutputPath))
            {
                throw new Exception("Input and output paths must be provided");
            }

            if (repacking)
            {
                if (!Directory.Exists(InputPath))
                {
                    throw new Exception("Input path must be a valid directory");
                }

                if (!IsPkg(OutputPath) && !IsZippedAppBundle(OutputPath))
                {
                    throw new Exception("Output path must be a .pkg or .app.zip file");
                }
            }
            if (!repacking)
            {
                if (!File.Exists(InputPath))
                {
                    throw new Exception("Input path must be a valid file");
                }

                if (!IsPkg(InputPath) && !IsUnzippedAppBundle(InputPath))
                {
                    throw new Exception("Input path must be a .pkg or .app file");
                }

                if (!Directory.Exists(OutputPath))
                {
                    Directory.CreateDirectory(OutputPath);
                }
            }

            if (IsPkg(InputPath))
            {
                UnpackedPkg unpackedPkg = new UnpackedPkg(repacking);
            }
            else if (IsAppBundle(InputPath))
            {
                ProcessAppBundle(repacking);
            }
        }

        internal static bool IsPkg(string path) =>
            Path.GetExtension(path).Equals(".pkg");

        internal static bool IsAppBundle(string path) =>
            IsZippedAppBundle(path) || IsUnzippedAppBundle(path);
        internal static bool IsZippedAppBundle(string path) =>
            Path.GetExtension(path).Equals(".zip") && Path.GetFileNameWithoutExtension(path).EndsWith(".app");

        internal static bool IsUnzippedAppBundle(string path) =>
            Path.GetExtension(path).Equals(".app");

        internal static void ProcessAppBundle(bool repacking, string inputPath = "", string outputPath = "")
        {
            if (string.IsNullOrEmpty(inputPath))
            {
                inputPath = InputPath;
            }
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = OutputPath;
            }
            if (!repacking)
            {
                // Unzip the .app.zip file to get the .app directory
                string args = $"-V -xk {inputPath} {outputPath}";
                ExecuteHelper.Run("ditto", args);
            }
            else
            {
                // Zip the .app directory to get the .app.zip file
                string args = $"-c -k --sequesterRsrc {inputPath} {outputPath}";
                ExecuteHelper.Run("ditto", args);
            }
        }

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
                throw new Exception($"Error finding file '{name}' in '{path}': {e.Message}");
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
        private string LocalExtractionPath;
        private string? Identifier = null;
        private string? Resources = null;
        private string? Distribution = null;
        private string? Scripts = null;
        private List<UnpackedBundle> Bundles = new List<UnpackedBundle>();

        internal UnpackedPkg(bool repacking)
        {
            if (repacking)
            {
                NameWithExtension = Path.GetFileName(Pkg.OutputPath);
                LocalExtractionPath = Pkg.InputPath;
            }
            else
            {
                NameWithExtension = Path.GetFileName(Pkg.InputPath);
                LocalExtractionPath = Pkg.OutputPath;
            }

            if (!repacking)
            {
                ExpandPkg();
            }

            Resources = Pkg.FindInPath("Resources", LocalExtractionPath, isDirectory: true, searchOption: SearchOption.TopDirectoryOnly);
            Distribution = Pkg.FindInPath("Distribution", LocalExtractionPath, isDirectory: false, searchOption: SearchOption.TopDirectoryOnly);
            Scripts = Pkg.FindInPath("Scripts", LocalExtractionPath, isDirectory: true, searchOption: SearchOption.TopDirectoryOnly);
            string? packageInfo = Pkg.FindInPath("PackageInfo", LocalExtractionPath, isDirectory: false, searchOption: SearchOption.TopDirectoryOnly);

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
                
                if(repacking)
                {
                    RepackPkg();
                }
            }
            else if (!string.IsNullOrEmpty(packageInfo))
            {
                // This is a single bundle package
                XElement pkgInfo = XElement.Load(packageInfo);
                Identifier = GetBundleId(pkgInfo);
                string version = pkgInfo.Attribute("version")?.Value ?? throw new Exception("No version found in PackageInfo file");
                Bundles.Add(new UnpackedBundle(LocalExtractionPath, Identifier, version, NameWithExtension, repacking, isNested: false));
            }
            else if (repacking)
            {
                throw new Exception("Cannot repack: no Distribution or PackageInfo file found in unpacked package");
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
            string args = string.Empty;
            args += $"--distribution {Distribution}";
            if (Bundles.Any())
            {
                args += $" --package-path {LocalExtractionPath}";
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

            if (File.Exists(Pkg.OutputPath))
            {
                File.Delete(Pkg.OutputPath);
            }
            args += $" {Pkg.OutputPath}";

            ExecuteHelper.Run("productbuild", args);
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
                    // so we remove this extension when unpacking the bundle.
                    // Otherwise, there will be problems when repacking the bundle due to the naming conflict
                    Directory.Move(localExtractionPath, LocalExtractionPath);
                }

                // When using pkgutil --expand, nested bundles are unpacked
                // so we need to repack these nested bundles during the unpacking process.
                // Otherwise, if the bundle is not nested, we need to repack it during the repacking process.
                if (!repacking || !isNested)
                {
                    // Only time we don't want to look for these files in a bundle is if we're repacking something nested
                    // This is because they've already been repacked
                    Scripts = Pkg.FindInPath("Scripts", LocalExtractionPath, isDirectory: true, searchOption: SearchOption.TopDirectoryOnly);
                    Payload = Pkg.FindInPath("Payload", LocalExtractionPath, isDirectory: repacking, searchOption: SearchOption.TopDirectoryOnly);

                    if (!string.IsNullOrEmpty(Payload) && !repacking)
                    {
                        UnpackPayloadFile(Path.GetFullPath(Payload));

                        IEnumerable<string> nestedApps = Pkg.GetDirectories(LocalExtractionPath, "*.app", SearchOption.AllDirectories);
                        foreach (string app in nestedApps)
                        {
                            string appZip = $"{app}.zip";
                            Pkg.ProcessAppBundle(repacking: true, inputPath: app, outputPath: appZip);
                            File.Delete(app);
                        }
                    }

                    if (repacking || isNested)
                    {
                        IEnumerable<string> zippedNestedApps = Pkg.GetDirectories(LocalExtractionPath, "*.app.zip", SearchOption.AllDirectories);
                        foreach (string appZip in zippedNestedApps)
                        {
                            string app = Path.Combine(Path.GetDirectoryName(appZip) ?? string.Empty, Path.GetFileNameWithoutExtension(appZip));
                            Pkg.ProcessAppBundle(repacking: false, inputPath: appZip, outputPath: app);
                            File.Delete(appZip);
                        }
                        PkgBuild(isNested);
                    }
                }

                if (!repacking && isNested)
                {
                    // We don't need the unpacked nested bundle
                    // anymore because we have repacked it
                    Directory.Delete(LocalExtractionPath, true);
                }
            }

            private void PkgBuild(bool isNested)
            {
                string info = GenerateInfoPlist();
                string root = string.IsNullOrEmpty(Payload) ? $"{LocalExtractionPath}" : $"{Payload}";
                string args = $"--root {root} --component-plist {info} --identifier {Identifier} --version {Version} --keychain login.keychain --install-location /usr/local/share/dotnet";
                if (!string.IsNullOrEmpty(Scripts))
                {
                    args += $" --scripts {Scripts}";
                }

                string outputPath = $"{LocalExtractionPath}.pkg";
                if (!isNested)
                {
                    outputPath = Pkg.OutputPath;
                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }
                }
                args += $" {outputPath}";

                ExecuteHelper.Run("pkgbuild", args);

                File.Delete(info);
            }

            private string GenerateInfoPlist()
            {
                string root = string.IsNullOrEmpty(Payload) ? $"{LocalExtractionPath}" : $"{Payload}";
                string info = Path.Combine(Pkg.WorkingDirectory, "Info.plist");
                ExecuteHelper.Run("pkgbuild", $"--analyze --root {root} {info}");
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

                    // Remove the payload file and replace it with
                    // a directory of the same name containing the unpacked contents
                    File.Delete(payloadFilePath);
                    Directory.Move(tempDir, payloadFilePath);
                }
            }
        }
    }
}
