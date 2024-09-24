// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.IO;
using System.Diagnostics;

#nullable enable

namespace Microsoft.DotNet.SourceBuild.Tasks.HtmlReport
{
    public static class Model
    {
        public static string AzdoOrg = "dnceng";
        public static string AzdoProject = "internal";
        public static string AzdoCommit = "commitSha";
        public static string AzdoRepo = "dotnet-dotnet";
        public static string AzdoUrl = $"https://dev.azure.com/{AzdoOrg}/{AzdoProject}/_git/{AzdoRepo}?version={AzdoCommit}&path={{0}}";
    }

    public class Repo
    {
        public string Name { get; set; } = string.Empty;
        public List<Package> Packages { get; set; } = new List<Package>();
    }

    public class Package
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public List<FileInfo> Files { get; set; } = new List<FileInfo>();
    }

    public class FileInfo
    {
        public string Repo { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Link { get; set; } = string.Empty;
        public string DependencyInfo { get; set; } = string.Empty;
        public string DependencyType
        {
            get
            {
                if (IsDirectDependency)
                {
                    return "Direct";
                }
                return "Transitive";
            }
        }
        public bool IsDirectDependency { get; set; } = false;

        public FileInfo(XmlNode usage, string repoRoot, string dotnetPath, bool isPipelineRun)
        {
            XmlAttributeCollection? attributes = usage.Attributes;
            if (attributes == null)
            {
                return;
            }

            string? filePath = attributes["File"]?.Value;
            
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            // For link, we need to find the .csproj file for the path + .csproj in the repo.
            // Given the filepath, we can find the file in the current repo.
            // Given that path, we form a link to the source.

            var parts = filePath.Split('/');
            Repo = parts[1];
            Path = parts[^2];

            Link = GetFileLink(filePath, repoRoot, isPipelineRun);

            IsDirectDependency = usage.Attributes?["IsDirectDependency"]?.Value == "true";

            DependencyInfo = !IsDirectDependency ? GetDependencyInfo(filePath, usage.Attributes?["Id"]?.Value, dotnetPath) : string.Empty;
        }

        private string GetDependencyInfo(string filePath, string? packageName, string dotnetPath)
        {
            if (packageName == null)
            {
                return string.Empty;
            }

            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = $"{dotnetPath}/dotnet",
                    Arguments = $"nuget why {filePath} {packageName}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            
            try
            {
                process.Start();
                string result = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return result;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error calling dotnet nuget why: {e.Message}");
                return string.Empty;
            }
        }

        private string GetFileLink(string filePath, string repoRoot, bool isPipelineRun)
        {
            // Find the file in the repo
            string relativePath = filePath.Split("/artifacts")[0];
            string fileName = filePath.Split("/")[^2] + ".csproj";
            string root = repoRoot + relativePath;

            IEnumerable<string> matchingFiles = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);

            if (!matchingFiles.Any())
            {
                return string.Empty;
            }

            string csprojPath = matchingFiles.First();

            if (isPipelineRun)
            {
                return string.Format(Model.AzdoUrl, csprojPath.Replace(repoRoot, ""));
            }

            return csprojPath;
        }
    }
}