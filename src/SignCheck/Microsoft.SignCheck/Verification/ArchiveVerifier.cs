// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.SignCheck.Logging;

namespace Microsoft.SignCheck.Verification
{
    public class ArchiveVerifier : FileVerifier
    {
        public ArchiveVerifier(Log log, Exclusions exclusions, SignatureVerificationOptions options, string fileExtension) : base(log, exclusions, options, fileExtension)
        {

        }

        /// <summary>
        /// Verify the contents of a zip-based archive and add the results to the container result.
        /// </summary>
        /// <param name="svr">The container result</param>
        protected void VerifyContent(SignatureVerificationResult svr)
        {
            if (VerifyRecursive)
            {
                string tempPath = svr.TempPath;
                CreateDirectory(tempPath);
                Log.WriteMessage(LogVerbosity.Diagnostic, SignCheckResources.DiagExtractingFileContents, tempPath);
                Dictionary<string, string> archiveMap = new Dictionary<string, string>();

                try
                {
                    foreach (ArchiveEntry entry in ExtractArchiveEntries(svr.FullPath, tempPath))
                    {
                        string aliasFullName = GenerateAlias(entry.RelativePath, tempPath);

                        bool wroteFile = WriteArchiveEntry(aliasFullName, entry);
                        if (wroteFile)
                        {
                            archiveMap[entry.RelativePath] = aliasFullName;
                        }
                    }

                    // We can only verify once everything is extracted from the container.
                    // This is mainly because MSIs can have mutliple external CAB files
                    // and we need to ensure they are extracted before we verify the MSIs.
                    foreach (string fullName in archiveMap.Keys)
                    {
                        SignatureVerificationResult result = VerifyFile(archiveMap[fullName], svr.Filename,
                            Path.Combine(svr.VirtualPath, fullName), fullName);

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
                    svr = SignatureVerificationResult.UnsupportedFileTypeResult(svr.FullPath, parent, svr.VirtualPath);
                    svr.AddDetail(DetailKeys.File, SignCheckResources.DetailSigned, SignCheckResources.NA);
                }
                finally
                {
                    DeleteDirectory(tempPath);
                }
            }
        }

        /// <summary>
        /// Extracts the contents of a zip-based archive to disk.
        /// </summary>
        protected virtual IEnumerable<ArchiveEntry> ExtractArchiveEntries(string archivePath, string extractionPath)
        {
            ZipFile.ExtractToDirectory(archivePath, extractionPath);
            foreach (var path in Directory.EnumerateFiles(extractionPath, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = path.Substring(extractionPath.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                using var stream = (Stream)File.Open(path, FileMode.Open);
                yield return new ArchiveEntry() { RelativePath = relativePath, Content = stream, ContentSize = stream?.Length ?? 0 };
            }
        }

        /// <summary>
        /// Generates an alias for the actual file that has the same extension.
        /// We do this to avoid path too long errors so that containers can be flattened.
        /// </summary>
        private string GenerateAlias(string fullPath, string tempPath)
        {
            string directoryName = Path.GetDirectoryName(fullPath);
            string hashedPath = String.IsNullOrEmpty(directoryName) ? Utils.GetHash(@".\", HashAlgorithmName.SHA256.Name) :
                Utils.GetHash(directoryName, HashAlgorithmName.SHA256.Name);
            string extension = Path.GetExtension(fullPath);

            // CAB files cannot be aliased since they're referred to from the Media table inside the MSI
            string aliasFileName = String.Equals(extension.ToLowerInvariant(), ".cab") ? Path.GetFileName(fullPath) :
                Utils.GetHash(fullPath, HashAlgorithmName.SHA256.Name) + Path.GetExtension(fullPath);
            return Path.Combine(tempPath, hashedPath, aliasFileName);
        }

        /// <summary>
        /// Parses a nested archive and writes it to disk.
        /// Returns true if the file was written, false if it already exists.
        /// </summary>
        private bool WriteArchiveEntry(string aliasFullName, ArchiveEntry entry)
        {
            if (File.Exists(aliasFullName))
            {
                Log.WriteMessage(LogVerbosity.Normal, SignCheckResources.FileAlreadyExists, aliasFullName);
                return false;
            }

            CreateDirectory(Path.GetDirectoryName(aliasFullName));
            File.WriteAllBytes(aliasFullName, ((MemoryStream)entry.Content).ToArray());
            return true;
        }

        protected class ArchiveEntry
        {
            public string RelativePath { get; set; }
            public Stream Content { get; set; }
            public long ContentSize { get; set; }
        }
    }
}
