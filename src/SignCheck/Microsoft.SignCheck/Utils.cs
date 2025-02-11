// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Microsoft.SignCheck
{
    public class Utils
    {
        /// <summary>
        /// Generate a hash for a string value using a given hash algorithm.
        /// </summary>
        /// <param name="value">The value to hash.</param>
        /// <param name="hashName">The name of the <see cref="HashAlgorithm"/> to use.</param>
        /// <returns>A string containing the hash result.</returns>
        public static string GetHash(string value, string hashName)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            HashAlgorithm ha = CreateHashAlgorithm(hashName);
            byte[] hash = ha.ComputeHash(bytes);

            var sb = new StringBuilder();
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        public static HashAlgorithm CreateHashAlgorithm(string hashName)
        {
            switch (hashName.ToUpperInvariant())
            {
                case "SHA256":
                    return SHA256.Create();
                case "SHA1":
                    return SHA1.Create();
                case "MD5":
                    return MD5.Create();
                case "SHA384":
                    return SHA384.Create();
                case "SHA512":
                    return SHA512.Create();
                default:
                    throw new ArgumentException("Unsupported hash algorithm name", nameof(hashName));
            }
        }

        /// <summary>
        /// Converts a string containing wildcards (*, ?) into a regular expression pattern string.
        /// </summary>
        /// <param name="wildcardPattern">The string pattern.</param>
        /// <returns>A string containing regular expression pattern.</returns>
        public static string ConvertToRegexPattern(string wildcardPattern)
        {
            string escapedPattern = Regex.Escape(wildcardPattern).Replace(@"\*", ".*").Replace(@"\?", ".");

            if ((wildcardPattern.EndsWith("*")) || (wildcardPattern.EndsWith("?")))
            {
                return escapedPattern;
            }
            else
            {
                return String.Concat(escapedPattern, "$");
            }            
        }

        public static string RunCommand(string command, bool throwOnError = true)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                process.WaitForExit(10000); // 10 seconds
                if (process.ExitCode != 0 && throwOnError)
                {
                    throw new Exception($"Command '{command}' failed with exit code {process.ExitCode}");
                }

                // Some processes write to stderr even if they succeed. 'gpg' is one such example
                return $"{output}{error}";
            }
        }

        /// <summary>
        /// Convert a Unix timestamp to a DateTime.
        /// </summary>
        public static DateTime GetDateTimeFromUnixTimestamp(string timestamp) =>
            DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestamp)).UtcDateTime;
    }
}
