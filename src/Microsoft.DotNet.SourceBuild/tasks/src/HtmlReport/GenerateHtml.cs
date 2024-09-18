// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Xml;

#nullable enable

namespace Microsoft.DotNet.SourceBuild.Tasks.HtmlReport
{
    public class GenerateHtmlReport
    {
        public static string PrebuiltReportFile { get; set; } = string.Empty;
        public static string OutputDirectory { get; set; } = string.Empty;
        public static bool IsPipelineRun { get; set; } = false;
        public static string DotNetPath { get; set; } = string.Empty;
        public static string RepositoryRoot { get; set; } = string.Empty;
        private static readonly string repoPathRegex = @"^src\/(?<repo>[^\/]+)";
        private static readonly string outputName = "output.html";

        public static async Task Main()
        {
            string outputPath = Path.Combine(OutputDirectory, outputName);

            if (!File.Exists(PrebuiltReportFile))
            {
                return;
            }

            if (!Directory.Exists(OutputDirectory))
            {;
                return;
            }

            try
            {
                await GenerateReport(outputPath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error generating report: {e.Message}");
                return;
            }
        }

        private static async Task GenerateReport(string outputPath)
        {
            IServiceCollection services = new ServiceCollection();
            services.AddLogging();

            IServiceProvider serviceProvider = services.BuildServiceProvider();
            ILoggerFactory loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

            

            // Create an HtmlRenderer instance and render the component
            await using var htmlRenderer = new HtmlRenderer(serviceProvider, loggerFactory);

            var repos = ReadPrebuiltUsageData();

            var html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
            {
                var dictionary = new Dictionary<string, object?>
                {
                    { "Repos", repos }
                };

                var parameters = ParameterView.FromDictionary(dictionary);
                var output = await htmlRenderer.RenderComponentAsync<PrebuiltReport>(parameters);

                return output.ToHtmlString();
            }
            );

            File.WriteAllText(outputPath, html);
        }

        private static List<Repo> ReadPrebuiltUsageData()
        {
            XmlDocument prebuiltUsageData = new XmlDocument();
            prebuiltUsageData.Load(PrebuiltReportFile);

            var repos = new Dictionary<string, List<Package>>();
            XmlNodeList repoNodes = prebuiltUsageData.SelectNodes("//ProjectDirectories/Dir") ?? throw new Exception("No project directories found");
            XmlNodeList usages = prebuiltUsageData.SelectNodes("//Usages/Usage") ?? throw new Exception("No usages found");

            // Create a dictionary of repo names to a list of packages
            foreach (XmlNode repoNode in repoNodes)
            {
                if (repoNode == null)
                {
                    continue;
                }

                string repoName = GetRepoName(repoNode.InnerText);
                repos.Add(repoName, new List<Package>());
            }

            // Populate the repo packages with the usage data
            foreach (XmlNode usage in usages)
            {
                if (usage == null || usage.Attributes == null)
                {
                    continue;
                }

                string repoName = GetRepoName(usage.Attributes["File"]?.Value);
                List<Package> packages = repos[repoName];

                string? packageName = usage.Attributes["Id"]?.Value;
                string? packageVersion = usage.Attributes["Version"]?.Value;

                if (packageName == null || packageVersion == null)
                {
                    continue;
                }

                // Find the package in the repo's list of packages or create a new one
                Package? package = packages.Find(
                        package => package.Name == packageName &&
                        package.Version == packageVersion);
                if (package == null)
                {
                    package = new Package
                    {
                        Name = packageName,
                        Version = packageVersion,
                    };
                    packages.Add(package);
                }
                package.Files.Add(new FileInfo(usage, RepositoryRoot, DotNetPath, IsPipelineRun));

                repos[repoName] = packages;
            }
            
            // return a list of Repo objects for each repo that contains a list of packages
            return repos
                .Where(repo => repo.Value.Count > 0)
                .Select(repo => new Repo
                {
                    Name = repo.Key,
                    Packages = repo.Value
                })
                .ToList();
        }

        private static string GetRepoName(string? filePath)
        {
            string unknownRepo = "Unknown";

            if (filePath == null)
            {
                return unknownRepo;
            }

            var match = Regex.Match(filePath, repoPathRegex);
            if (!match.Success)
            {
                if (!filePath.Contains("src/"))
                {
                    return unknownRepo;
                }
                
                // This case is used when the prebuilt report is generated
                // for a product repo and not generated for the VMR (dotnet repo)
                return "Current Repo";
            }
            return match.Groups["repo"].Value;
        }
    }
}
