// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.SignCheck.Logging;

namespace Microsoft.SignCheck.Verification
{
    public abstract class ArchiveVerifier : FileVerifier
    {
        private readonly bool _supportsDetachedSignatureVerification;

        protected ArchiveVerifier(Log log, Exclusions exclusions, SignatureVerificationOptions options, string fileExtension, bool supportsDetachedSignatureVerification = false) : base(log, exclusions, options, fileExtension)
        {
            _supportsDetachedSignatureVerification = supportsDetachedSignatureVerification;
        }

        /// <summary>
        /// Read the entries from the archive.
        /// </summary>
        /// <param name="archivePath">Path to the archive</param>
        protected abstract IEnumerable<ArchiveEntry> ReadArchiveEntries(string archivePath);

        /// <summary>
        /// Returns the paths to the detached signature document and the corresponding signable content.
        /// The default implementation assumes an external detached signature file next to the archive.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="tempDir"></param>
        /// <returns></returns>
        protected virtual (string signatureDocument, string signableContent) GetSignatureDocumentAndSignableContent(string path, string tempDir)
        {
            if (!_supportsDetachedSignatureVerification)
            {
                throw new Exception("File type does not support detached signature verification");
            }

            string signatureDocument = $"{path}.sig";
            string signableContent = path;

            if (!File.Exists(signatureDocument))
            {
                throw new FileNotFoundException($"Detached signature document {signatureDocument} does not exists");
            }

            return (signatureDocument, signableContent);
        }

        /// <summary>
        /// Verifies the signature of a supported file type.
        /// </summary>
        /// <param name="context">The file verification input context.</param>
        protected SignatureVerificationResult VerifySupportedFileType(FileVerificationContext context)
        {
            try
            {
                SignatureVerificationResult svr = new SignatureVerificationResult(context);

                svr.IsSigned = IsSigned(svr.FullPath, svr);
                svr.AddDetail(DetailKeys.File, SignCheckResources.DetailSigned, svr.IsSigned);

                VerifyContent(svr);
                return svr;
            }
            catch (PlatformNotSupportedException)
            {
                // Verification is not supported on all platforms for all file types
                return VerifyUnsupportedFileType(context);
            }
        }

        /// <summary>
        /// Verifies the signature of an unsupported file type.
        /// </summary>
        protected SignatureVerificationResult VerifyUnsupportedFileType(FileVerificationContext context)
        {
            var result = SignatureVerificationResult.UnsupportedFileTypeResult(context);
            if (!_supportsDetachedSignatureVerification && context.HasDetachedSignature)
            {
                result.AddDetail(DetailKeys.Error, "Detached signature verification is not supported for this archive type but file has a detached signature.");
            }

            result.AddDetail(DetailKeys.File, SignCheckResources.DetailSigned, SignCheckResources.NA);
            VerifyContent(result);
            return result;
        }

        protected virtual bool IsDetachedSignatureSigned(string path, SignatureVerificationResult svr)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Detached signature verification is not supported on Windows.");
            }

            string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            try
            {
#if NET
                Utils.DownloadAndConfigurePublicKeys(tempDir);
#endif
                (string signatureDocument, string signableContent) = GetSignatureDocumentAndSignableContent(path, tempDir);

                if (string.IsNullOrEmpty(signatureDocument) || string.IsNullOrEmpty(signableContent))
                {
                    return false;
                }

                if (!File.Exists(signatureDocument))
                {
                    svr.AddDetail(DetailKeys.Error, $"Detached signature file '{signatureDocument}' was not found.");
                    return false;
                }

                if (!VerifyDetachedSignature(signatureDocument, signableContent, svr, out string verificationOutput))
                {
                    return false;
                }

                return AddGpgTimestamp(verificationOutput, svr);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        protected bool VerifyDetachedSignature(string signatureDocument, string signableContent, SignatureVerificationResult svr, out string verificationOutput)
        {
            (int exitCode, string output, string error) = Utils.RunBashCommand($"gpg --verify --status-fd 1 {signatureDocument} {signableContent}");
            verificationOutput = output + error;

            if (verificationOutput.Contains("Good signature"))
            {
                return true;
            }

            if (exitCode != 0 && !verificationOutput.Contains("no signature found"))
            {
                svr.AddDetail(DetailKeys.Error, error);
            }

            return false;
        }

        protected bool AddGpgTimestamp(string verificationOutput, SignatureVerificationResult svr)
        {
            Timestamp ts = GetTimestamp(verificationOutput);
            ts.AddToSignatureVerificationResult(svr);
            return ts.IsValid;
        }

        /// <summary>
        /// Get the timestamp of the signature in the package.
        /// </summary>
        private Timestamp GetTimestamp(string verificationOutput)
        {
            Regex signatureTimestampsRegex = new Regex(@"VALIDSIG .+ \d+-\d+-\d+ (?<signedOn>\d+) (?<expiresOn>\d+) ");
            Match signatureTimestampsMatch = signatureTimestampsRegex.Match(verificationOutput);

            Regex signatureKeyInfoRegex = new Regex(@"using (?<algorithm>.+) key (?<keyId>.+)");
            Match signatureKeyInfoMatch = signatureKeyInfoRegex.Match(verificationOutput);

            string keyId = signatureKeyInfoMatch.GroupValueOrDefault("keyId");
            (_, string keyInfo, _) = Utils.RunBashCommand($"gpg --list-keys --with-colons {keyId} | grep '^pub:'");
            Regex keyInfoRegex = new Regex(@$"pub.+{keyId}:(?<createdOn>\d+):");
            Match keyInfoMatch = keyInfoRegex.Match(keyInfo);

            return new Timestamp()
            {
                SignedOn = signatureTimestampsMatch.GroupValueOrDefault("signedOn").DateTimeOrDefault(DateTime.MaxValue),
                ExpiryDate = signatureTimestampsMatch.GroupValueOrDefault("expiresOn").DateTimeOrDefault(DateTime.MaxValue),
                SignatureAlgorithm = signatureKeyInfoMatch.GroupValueOrDefault("algorithm"),
                EffectiveDate = keyInfoMatch.GroupValueOrDefault("createdOn").DateTimeOrDefault(DateTime.MaxValue)
            };
        }

        /// <summary>
        /// Verifies the signature of the archive.
        /// </summary>
        /// <param name="path">The path of the archive.</param>
        /// <param name="svr">The signature verification result.</param>
        protected virtual bool IsSigned(string path, SignatureVerificationResult svr)
        {
            if (_supportsDetachedSignatureVerification)
            {
                return IsDetachedSignatureSigned(path, svr);
            }

            throw new NotImplementedException();
        }

        /// <summary>
        /// Verify the contents of a package archive and add the results to the container result.
        /// </summary>
        /// <param name="svr">The container result</param>
        protected void VerifyContent(SignatureVerificationResult svr)
        {
            if (VerifyRecursive)
            {
                svr.IsDoNotUnpack = Exclusions.IsDoNotUnpack(new FileVerificationContext(
                    svr.FullPath,
                    Path.GetDirectoryName(svr.FullPath) ?? SignCheckResources.NA,
                    svr.VirtualPath,
                    svr.VirtualPath));

                if (svr.IsDoNotUnpack)
                {
                    Log.WriteMessage(LogVerbosity.Detailed, SignCheckResources.DiagSkippingArchiveExtraction, svr.FullPath);
                    return;
                }

                string tempPath = svr.TempPath;
                CreateDirectory(tempPath);
                Log.WriteMessage(LogVerbosity.Diagnostic, SignCheckResources.DiagExtractingFileContents, tempPath);
                Dictionary<string, string> archiveMap = new Dictionary<string, string>();

                try
                {
                    foreach (ArchiveEntry archiveEntry in ReadArchiveEntries(svr.FullPath))
                    {
                        if (archiveEntry.IsEmptyOrInvalid())
                        {
                            var result = SignatureVerificationResult.UnsupportedFileTypeResult(new FileVerificationContext(
                                archiveEntry.RelativePath,
                                svr.VirtualPath,
                                Path.Combine(svr.VirtualPath, archiveEntry.RelativePath),
                                containerPath: null));

                            result.AddDetail(DetailKeys.Misc, "Empty or invalid archive entry");
                            svr.NestedResults.Add(result);
                            continue;
                        }

                        string aliasFullName = GenerateArchiveEntryAlias(archiveEntry, tempPath);
                        if (File.Exists(aliasFullName))
                        {
                            Log.WriteMessage(LogVerbosity.Normal, SignCheckResources.FileAlreadyExists, aliasFullName);
                        }
                        else
                        {
                            CreateDirectory(Path.GetDirectoryName(aliasFullName));
                            WriteArchiveEntry(archiveEntry, aliasFullName);
                            archiveMap[archiveEntry.RelativePath] = aliasFullName;
                        }
                    }

                    // We can only verify once everything is extracted. This is mainly because MSIs can have mutliple external CAB files
                    // and we need to ensure they are extracted before we verify the MSIs.
                    foreach (string fullName in archiveMap.Keys)
                    {
                        SignatureVerificationResult result = VerifyFile(new FileVerificationContext(
                            archiveMap[fullName],
                            svr.VirtualPath,
                            Path.Combine(svr.VirtualPath, fullName),
                            fullName));

                        // Tag the full path into the result detail
                        result.AddDetail(DetailKeys.File, SignCheckResources.DetailFullName, fullName);
                        svr.NestedResults.Add(result);
                    }
                }
                catch (PlatformNotSupportedException)
                {
                    // Log the error and return an unsupported file type result
                    // because some archive types are not supported on all platforms
                    string parent = Path.GetDirectoryName(svr.FullPath) ?? SignCheckResources.NA;
                    svr = SignatureVerificationResult.UnsupportedFileTypeResult(new FileVerificationContext(svr.FullPath, parent, svr.VirtualPath, containerPath: null));
                    svr.AddDetail(DetailKeys.File, SignCheckResources.DetailSigned, SignCheckResources.NA);
                }
                finally
                {
                    DeleteDirectory(tempPath);
                }
            }
        }

        /// <summary>
        /// Writes the archive entry to the specified path.
        /// </summary>
        /// <param name="archiveEntry"></param>
        /// <param name="targetPath"></param>
        protected virtual void WriteArchiveEntry(ArchiveEntry archiveEntry, string targetPath)
        {
            using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
            {
                archiveEntry.ContentStream.CopyTo(fileStream);
            }
        }

        /// <summary>
        /// Generates an alias for the actual file that has the same extension.
        /// We do this to avoid path too long errors so that containers can be flattened.
        /// </summary>
        /// <param name="archiveEntry">The archive entry to generate the alias for.</param>
        /// <param name="tempPath">The temporary path for the archive entry.</param>
        private string GenerateArchiveEntryAlias(ArchiveEntry archiveEntry, string tempPath)
        {
            // Generate an alias for the actual file that has the same extension. We do this to avoid path too long errors so that
            // containers can be flattened.
            string directoryName = Path.GetDirectoryName(archiveEntry.RelativePath);
            string hashedPath = String.IsNullOrEmpty(directoryName) ? Utils.GetHash(@".\", HashAlgorithmName.SHA256.Name) :
                Utils.GetHash(directoryName, HashAlgorithmName.SHA256.Name);
            string extension = Path.GetExtension(archiveEntry.RelativePath);

            // CAB files cannot be aliased since they're referred to from the Media table inside the MSI
            string aliasFileName = String.Equals(extension.ToLowerInvariant(), ".cab") ? Path.GetFileName(archiveEntry.RelativePath) :
                Utils.GetHash(archiveEntry.RelativePath, HashAlgorithmName.SHA256.Name) + Path.GetExtension(archiveEntry.RelativePath); // lgtm [cs/zipslip] Archive from trusted source

            return Path.Combine(tempPath, hashedPath, aliasFileName);
        }

        /// <summary>
        /// Represents an entry in an archive.
        /// </summary>
        protected class ArchiveEntry
        {
            public string RelativePath { get; set; } = string.Empty;
            public Stream ContentStream { get; set; } = Stream.Null;
            public long ContentSize { get; set; } = 0;

            public bool IsEmptyOrInvalid()
                => string.IsNullOrEmpty(RelativePath) || ContentStream == Stream.Null || ContentSize == 0;
        }
    }
}
