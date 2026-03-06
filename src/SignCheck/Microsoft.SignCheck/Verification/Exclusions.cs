// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Microsoft.SignCheck.Verification
{
    public class Exclusions
    {
        /// <summary>
        /// Cache for regex exclusions.
        /// Helps avoid recompiling the regex for the same pattern multiple times.
        /// </summary>
        private Dictionary<string, Regex> _regexCache = new Dictionary<string, Regex>();
        private static readonly char[] _wildCards = new char[] { '*', '?' };
        private List<Exclusion> _exclusions = new List<Exclusion>();

        private const string DoNotSign = "DO-NOT-SIGN";
        private const string General = "GENERAL";
        private const string IgnoreStrongName = "IGNORE-STRONG-NAME";
        private const string DoNotUnpack = "DO-NOT-UNPACK";

        public int Count
        {
            get
            {
                return _exclusions.Count;
            }
        }

        public Exclusions()
        {

        }

        /// <summary>
        /// Creates a collection of <see cref="Exclusion"/>s from a text file. 
        /// </summary>
        /// <param name="path">Path to a file that contains exclusion entries.</param>
        public Exclusions(string path)
        {
            if (File.Exists(path))
            {
                using (StreamReader fileReader = File.OpenText(path))
                {
                    string line = fileReader.ReadLine();

                    while (line != null)
                    {
                        if (!String.IsNullOrEmpty(line))
                        {
                            Add(new Exclusion(line));
                        }
                        line = fileReader.ReadLine();
                    }
                }
            }
        }

        public void Add(Exclusion exclusion)
        {
            if (!_exclusions.Contains(exclusion))
            {
                _exclusions.Add(exclusion);
            }
        }

        public void Clear()
        {
            _exclusions.Clear();
        }

        public bool Contains(Exclusion exclusion)
        {
            return _exclusions.Contains(exclusion);
        }

        private bool IsExcluded(FileVerificationContext context, string exclusionClassification, IEnumerable<Exclusion> exclusions)
        {
            foreach (var exclusion in exclusions)
            {
                // 1. The file/container path matches a file part of the exclusion and the parent matches the parent part of the exclusion.
                //    Example: bar.dll;*.zip --> Exclude any occurence of bar.dll that is in a zip file
                //             bar.dll;foo.zip --> Exclude bar.dll only if it is contained inside foo.zip
                //             foo.exe;; --> Exclude any occurance of foo.exe and ignore the parent
                if (IsFileExcluded(exclusion, context, exclusionClassification) &&
                    (!exclusion.HasParentFiles || IsParentExcluded(exclusion, context.Parent, exclusionClassification)))
                {
                    return true;
                }

                // 2. There is no file exclusion, but a parent exclusion matches.
                //    Example: ;foo.zip; --> Exclude any file in foo.zip. This is similar to using *;foo.zip;
                if (!exclusion.HasFilePatterns && IsParentExcluded(exclusion, context.Parent, exclusionClassification))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Return true if an exclusion matches the file path, parent file container or the path in the container
        /// </summary>
        /// <param name="context">The file verification input context.</param>
        /// <returns></returns>
        public bool IsExcluded(FileVerificationContext context)
        {
            IEnumerable<Exclusion> exclusions = _exclusions.Where(e => !e.Comment.Contains(IgnoreStrongName) && !e.Comment.Contains(DoNotUnpack));
            return IsExcluded(context, General, exclusions);
        }

        public bool IsExcluded(string path, string parent, string virtualPath, string containerPath)
            => IsExcluded(new FileVerificationContext(path, parent, virtualPath, containerPath));

        /// <summary>
        /// Returns true if the file pattern matches the file and the exclusion comment contains DO-NOT-SIGN.
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public bool IsDoNotSign(FileVerificationContext context)
        {
            // Get all the exclusions with DO-NOT-SIGN markers and check only against those
            IEnumerable<Exclusion> doNotSignExclusions = _exclusions.Where(e => e.Comment.Contains(DoNotSign)).ToArray();

            return (doNotSignExclusions.Count() > 0) && (IsExcluded(context, DoNotSign, doNotSignExclusions));
        }

        public bool IsDoNotSign(string path, string parent, string virtualPath, string containerPath)
            => IsDoNotSign(new FileVerificationContext(path, parent, virtualPath, containerPath));

        public bool IsIgnoreStrongName(FileVerificationContext context)
        {
            // Get all the exclusions with NO-STRONG-NAME markers and check only against those
            IEnumerable<Exclusion> noStrongNameExclusions = _exclusions.Where(e => e.Comment.Contains(IgnoreStrongName));

            return (noStrongNameExclusions.Count() > 0) && (IsExcluded(context, IgnoreStrongName, noStrongNameExclusions));
        }

        public bool IsIgnoreStrongName(string path, string parent, string virtualPath, string containerPath)
            => IsIgnoreStrongName(new FileVerificationContext(path, parent, virtualPath, containerPath));

        public bool IsDoNotUnpack(FileVerificationContext context)
        {
            // Get all the exclusions with DO-NOT-UNPACK markers and check only against those
            IEnumerable<Exclusion> doNotUnpackExclusions = _exclusions.Where(e => e.Comment.Contains(DoNotUnpack));

            return (doNotUnpackExclusions.Count() > 0) && (IsExcluded(context, DoNotUnpack, doNotUnpackExclusions));
        }

        public bool IsDoNotUnpack(string path, string parent, string virtualPath, string containerPath)
            => IsDoNotUnpack(new FileVerificationContext(path, parent, virtualPath, containerPath));

        /// <summary>
        /// Returns true if any <see cref="Exclusion.FilePatterns"/> matches the value of
        /// <paramref name="path"/>, <paramref name="containerPath"/> or <paramref name="virtualPath"/>.
        /// </summary>
        /// <param name="path">The value to match against <see cref="Exclusion.FilePatterns"/>.</param>
        /// <param name="containerPath">The value to match against <see cref="Exclusion.FilePatterns"/>.</param>
        /// <param name="virtualPath">The value to match against <see cref="Exclusion.FilePatterns"/>.</param>
        /// <returns></returns>
        public bool IsFileExcluded(Exclusion exclusion, FileVerificationContext context, string exclusionsClassification)
        {
            var values = new[]
            {
                context.Path,
                context.ContainerPath,
                context.VirtualPath,
                Path.GetFileName(context.Path),
                Path.GetFileName(context.ContainerPath),
                Path.GetFileName(context.VirtualPath)
            };

            if(!exclusion.TryGetIsFileExcluded(exclusionsClassification, values, out bool isExcluded))
            {
                isExcluded = values.Any(v => IsMatch(exclusion.FilePatterns, v));
                exclusion.AddToFileCache(exclusionsClassification, values, isExcluded);
            }

            return isExcluded;
        }

        /// <summary>
        /// Returns true if any <see cref="Exclusion.ParentFiles"/> matches the value of <paramref name="parent"/>.
        /// </summary>
        /// <param name="parent">The value to match against <see cref="Exclusion.ParentFiles"/>.</param>
        /// <returns></returns>
        public bool IsParentExcluded(Exclusion exclusion, string parent, string exclusionsClassification)
        {
            if(!exclusion.TryGetIsParentExcluded(exclusionsClassification, parent, out bool isExcluded))
            {
                isExcluded = IsMatch(exclusion.ParentFiles, parent);
                exclusion.AddToParentCache(exclusionsClassification, parent, isExcluded);
            }
    
            return isExcluded;
        }

        private bool IsMatch(string[] patterns, string value)
        {
            return patterns.Any(p => IsMatch(p, value));
        }

        private bool IsMatch(string pattern, string value)
        {
            if (String.IsNullOrEmpty(pattern) || String.IsNullOrEmpty(value))
            {
                return false;
            }

            if (pattern.IndexOfAny(_wildCards) > -1)
            {
                var regex = GetRegex(pattern);
                return regex.IsMatch(value);
            }
            else
            {
                return String.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool Remove(Exclusion exclusion)
        {
            return false;
        }

        private Regex GetRegex(string pattern)
        {
            if (!_regexCache.ContainsKey(pattern))
            {
                string regexPattern = Utils.ConvertToRegexPattern(pattern);
                _regexCache[pattern] = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            }
            return _regexCache[pattern];
        }
    }
}
