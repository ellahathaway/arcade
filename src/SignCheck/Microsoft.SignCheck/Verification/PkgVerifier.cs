// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.SignCheck.Logging;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace Microsoft.SignCheck.Verification
{
    public class PkgVerifier : ArchiveVerifier
    {
        private readonly StringBuilder _output = new StringBuilder();
        private readonly StringBuilder _error = new StringBuilder();

        public PkgVerifier(Log log, Exclusions exclusions, SignatureVerificationOptions options, string fileExtension) : base(log, exclusions, options, fileExtension)
        {
            if (fileExtension != ".pkg" && fileExtension != ".app")
            {
                throw new ArgumentException("PkgVerifier can only be used with .pkg and .app files.");
            }
        }

        public override SignatureVerificationResult VerifySignature(string path, string parent, string virtualPath) 
        {
            SignatureVerificationResult svr = new SignatureVerificationResult(path, parent, virtualPath);
            string fullPath = svr.FullPath;

            try
            {
                svr.IsSigned = IsSigned(fullPath);
                svr.AddDetail(DetailKeys.File, SignCheckResources.DetailSigned, svr.IsSigned);

                if (!svr.IsSigned && HasLoggedError())
                {
                    svr.AddDetail(DetailKeys.Error, _error.ToString());
                }
            }
            catch (PlatformNotSupportedException)
            {
                // Log the error and return an unsupported file type result
                // because processing pkgs and apps is not supported on non-OSX platforms
                svr = SignatureVerificationResult.UnsupportedFileTypeResult(path, parent, virtualPath);
                svr.AddDetail(DetailKeys.File, SignCheckResources.DetailSigned, SignCheckResources.NA);
            }

            foreach (Timestamp ts in GetTimestamps())
            {
                if (ts.IsValid)
                {
                    svr.AddDetail(DetailKeys.Misc, SignCheckResources.DetailTimestamp, ts.SignedOn, ts.SignatureAlgorithm);
                }
                else
                {
                    if (ts.SignedOn == DateTime.MaxValue || ts.SignedOn == DateTime.MinValue)
                    {
                        svr.AddDetail(DetailKeys.Error, SignCheckResources.ErrorInvalidOrMissingTimestamp);
                    }
                    else
                    {
                        svr.AddDetail(DetailKeys.Error, SignCheckResources.DetailTimestampOutisdeCertValidity, ts.SignedOn, ts.EffectiveDate, ts.ExpiryDate);
                    }
                }
            }

            VerifyContent(svr);

            return svr;
        }
        
        protected override IEnumerable<ArchiveEntry> ExtractArchiveEntries(string archivePath, string extractionPath)
        {
            if (!RunPkgProcess(archivePath, extractionPath, "unpack"))
            {
                throw new Exception($"Failed to unpack pkg '{archivePath}'");
            }

            foreach (var path in Directory.EnumerateFiles(extractionPath, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = path.Substring(extractionPath.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                using var stream = (Stream)File.Open(path, FileMode.Open);
               yield return new ArchiveEntry() { RelativePath = relativePath, Content = stream, ContentSize = stream?.Length ?? 0 };
            }
        }

        private bool IsSigned(string path) => RunPkgProcess(path, null, "verify");

        /// <summary>
        /// Get the timestamps from the output of the pkgutil command.
        /// Assumes that the verify command has already been run.
        /// </summary>
        private IEnumerable<Timestamp> GetTimestamps()
        {
            string output = _output.ToString();
            string timestampRegex = @"(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \+\d{4})";

            Regex signedOnRegex = new Regex(@"Signed with a trusted timestamp on: " + timestampRegex);
            string signedOnTimestamp = GetRegexValue(signedOnRegex.Match(output), "timestamp");

            Regex certificateChainRegex = new Regex(@"Expires: " + timestampRegex + "\n (?<algorithm>.+) Fingerprint:");
            IEnumerable<Match> matches = certificateChainRegex.Matches(output).ToList();

            return matches.Select(match =>
                {
                    string certificateTimestamp = GetRegexValue(match, "timestamp");
                    return new Timestamp(
                        effectiveDate: signedOnTimestamp,
                        expiryDate: certificateTimestamp,
                        signedOn: signedOnTimestamp,
                        signatureAlgorithm: GetRegexValue(match, "algorithm")
                    );
                });
        }

        private string GetRegexValue(Match match, string groupName) =>
            match.Success ? match.Groups[groupName].Value : null;

        private bool HasLoggedError() => !String.IsNullOrEmpty(_error.ToString());

        internal bool RunPkgProcess(string srcPath, string dstPath, string action)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                throw new PlatformNotSupportedException($"Pkg tooling is only supported on MacOS.");
            }

            if (string.IsNullOrEmpty(SignatureVerificationManager.DotNetPath) || string.IsNullOrEmpty(SignatureVerificationManager.PkgToolPath))
            {
                throw new ArgumentException($"DotNetPath and PkgToolPath must be set in order to run the pkg tool.");
            }

            if (!File.Exists(SignatureVerificationManager.DotNetPath))
            {
                throw new FileNotFoundException($"DotNetPath '{SignatureVerificationManager.DotNetPath}' does not exist.");
            }

            if (!File.Exists(SignatureVerificationManager.PkgToolPath))
            {
                throw new FileNotFoundException($"PkgToolPath '{SignatureVerificationManager.PkgToolPath}' does not exist.");
            }

            string args = $@"{action} ""{srcPath}""";
            
            if (action != "verify")
            {
                args += $@" ""{dstPath}""";
            }

            var process = Process.Start(new ProcessStartInfo()
            {
                FileName = SignatureVerificationManager.DotNetPath,
                Arguments = $@"exec ""{SignatureVerificationManager.PkgToolPath}"" {args}",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            process.OutputDataReceived += (sender, e) => _output.AppendLine(e.Data);
            process.BeginOutputReadLine();

            process.ErrorDataReceived += (sender, e) => _error.AppendLine(e.Data);
            process.BeginErrorReadLine();

            process.WaitForExit(60000); // 1 minute
            return process.ExitCode == 0;
        }
    }
}
