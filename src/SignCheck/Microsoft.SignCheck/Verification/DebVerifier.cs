// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.DotNet.Build.Tasks.Installers;
using Microsoft.SignCheck.Logging;

namespace Microsoft.SignCheck.Verification
{
    public class DebVerifier : ArchiveVerifier
    {
        private static readonly HttpClient s_client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) });

        public DebVerifier(Log log, Exclusions exclusions, SignatureVerificationOptions options) : base(log, exclusions, options, ".deb") { }

        public override SignatureVerificationResult VerifySignature(string path, string parent, string virtualPath) 
        {
            SignatureVerificationResult svr = new SignatureVerificationResult(path, parent, virtualPath);
            string fullPath = svr.FullPath;

            try
            {
                svr.IsSigned = IsSigned(fullPath, svr);
                svr.AddDetail(DetailKeys.File, SignCheckResources.DetailSigned, svr.IsSigned);
            }
            catch (PlatformNotSupportedException)
            {
                // Log the error and return an unsupported file type result
                // because processing debs are not supported on non-Linux platforms
                svr = SignatureVerificationResult.UnsupportedFileTypeResult(path, parent, virtualPath);
                svr.AddDetail(DetailKeys.File, SignCheckResources.DetailSigned, SignCheckResources.NA);
            }

            VerifyContent(svr);

            return svr;
        }

        protected override IEnumerable<ArchiveEntry> ExtractArchiveEntries(string archivePath, string extractionPath)
         => ReadDebContainerEntries(archivePath, "data.tar");

        private bool IsSigned(string path, SignatureVerificationResult result)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Deb verification is not supported on Windows.");
            }

            string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            // https://microsoft.sharepoint.com/teams/prss/esrp/info/SitePages/Linux%20GPG%20Signing.aspx
            try
            {
                DownloadAndConfigureMicrosoftPublicKey(tempDir);

                string debianBinary = ExtractDebContainerEntry(path, "debian-binary", tempDir);
                string controlTar = ExtractDebContainerEntry(path, "control.tar", tempDir);
                string dataTar = ExtractDebContainerEntry(path, "data.tar", tempDir);
                Utils.RunCommand($"cat {debianBinary} {controlTar} {dataTar} > {tempDir}/combined-contents");

                string gpgOrigin = ExtractDebContainerEntry(path, "_gpgorigin", tempDir);
                string verificationOutput = Utils.RunCommand($"gpg --verify {gpgOrigin} {tempDir}/combined-contents", throwOnError: false);

                // Verify the signature
                if (!verificationOutput.Contains("Good signature"))
                {
                    return false;
                }

                // Verify the timestamp of the signature
                Timestamp ts = GetTimestamp(path, verificationOutput);
                if (ts.IsValid)
                {
                    result.AddDetail(DetailKeys.Misc, SignCheckResources.DetailTimestamp, ts.SignedOn, ts.SignatureAlgorithm);
                }
                else
                {
                    if (ts.SignedOn == DateTime.MaxValue || ts.SignedOn == DateTime.MinValue)
                    {
                        result.AddDetail(DetailKeys.Error, SignCheckResources.ErrorInvalidOrMissingTimestamp);
                    }
                    else
                    {
                        result.AddDetail(DetailKeys.Error, SignCheckResources.DetailTimestampOutisdeCertValidity, ts.SignedOn, ts.EffectiveDate, ts.ExpiryDate);
                    }
                    return false;
                }

                return true;
            }
            catch(Exception e)
            {
                result.AddDetail(DetailKeys.Error, e.Message);
                return false;
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        /// <summary>
        /// Get the timestamps of the signatures in the deb package.
        /// Requires that the microsoft.asc key has been imported into the keyring.
        /// </summary>
        private Timestamp GetTimestamp(string archivePath, string verificationOutput)
        {
            Regex signatureRegex = new Regex(@"Signature made (?<signedOn>.+)\n\s*gpg:\s*using (?<algorithm>.+) key (?<keyId>.+)");
            Match signatureMatch = signatureRegex.Match(verificationOutput);
            if (!signatureMatch.Success)
            {
                Log.WriteError(LogVerbosity.Normal, $"Failed to extract signature information from '{archivePath}'");
                return new Timestamp();
            }

            string format = "ddd dd MMM yyyy hh:mm:ss tt 'UTC'";
            DateTime signedOn = DateTime.ParseExact(signatureMatch.Groups["signedOn"].Value, format, CultureInfo.InvariantCulture);

            string algorithm = signatureMatch.Groups["algorithm"].Value;

            // https://git.gnupg.org/cgi-bin/gitweb.cgi?p=gnupg.git;a=blob_plain;f=doc/DETAILS
            string keyInfo = Utils.RunCommand($"gpg --list-keys --with-colons {signatureMatch.Groups["keyId"].Value} | grep '^pub:'");

            // Field 6: Creation time
            // Field 7: Expiry time - empty for keys that do not expire
            Regex keyTimestamps = new Regex(@".+:.+:.+:.+:.+:(?<created>\d+):(?<expires>\d*):");
            Match keyMatch = keyTimestamps.Match(keyInfo);
            if (!keyMatch.Success)
            {
                Log.WriteError(LogVerbosity.Normal, $"Failed to extract key information from '{archivePath}'");
                return new Timestamp()
                {
                    SignedOn=signedOn,
                    SignatureAlgorithm=algorithm
                };
            }

            string created = keyMatch.Groups["created"].Value;
            string expires = keyMatch.Groups["expires"].Value;

            return new Timestamp()
            {
                EffectiveDate=Utils.GetDateTimeFromUnixTimestamp(created),
                ExpiryDate=string.IsNullOrEmpty(expires) ? DateTime.MaxValue : Utils.GetDateTimeFromUnixTimestamp(expires),
                SignedOn=signedOn,
                SignatureAlgorithm=algorithm
            };
        }

        /// <summary>
        /// Read the entries in the deb container.
        /// </summary>
        private IEnumerable<ArchiveEntry> ReadDebContainerEntries(string archivePath, string match = null)
        {
            using var archive = new ArReader(File.OpenRead(archivePath), leaveOpen: false);

            while (archive.GetNextEntry() is ArEntry entry)
            {
                string relativePath = entry.Name; // lgtm [cs/zipslip] Archive from trusted source

                // The relative path ocassionally ends with a '/', which is not a valid path given that the path is a file.
                // Remove the following workaround once https://github.com/dotnet/arcade/issues/15384 is resolved.
                if (relativePath.EndsWith("/"))
                {
                    relativePath = relativePath.TrimEnd('/');
                }

                if (match == null || relativePath.StartsWith(match))
                {
                    yield return new ArchiveEntry()
                    {
                        RelativePath = relativePath,
                        Content = entry.DataStream,
                        ContentSize = entry.DataStream.Length
                    };
                }
            }
        }

        /// <summary>
        /// Extract a single entry from the deb container.
        /// </summary>
        private string ExtractDebContainerEntry(string archivePath, string entryName, string workingDir)
        {
            ArchiveEntry archiveEntry = ReadDebContainerEntries(archivePath, entryName).Single();
            string entryPath = Path.Combine(workingDir, archiveEntry.RelativePath);
            File.WriteAllBytes(entryPath, ((MemoryStream)archiveEntry.Content).ToArray());

            return entryPath;
        }

        /// <summary>
        /// Download the Microsoft public key and import it into the keyring.
        /// </summary>
        public static void DownloadAndConfigureMicrosoftPublicKey(string tempDir)
        {
            using (Stream stream = s_client.GetStreamAsync("https://packages.microsoft.com/keys/microsoft.asc").Result)
            {
                using (FileStream fileStream = File.Create($"{tempDir}/microsoft.asc"))
                {
                    stream.CopyTo(fileStream);
                }
            }
            Utils.RunCommand($"gpg --import {tempDir}/microsoft.asc");
        }
    }
}
