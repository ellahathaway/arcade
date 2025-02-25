// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Microsoft.DotNet.SignTool
{
    public enum SigningToolErrorCode
    {
        /// <summary>
        /// Signing a Microsoft library with a 3rd party cert
        /// </summary>
        SIGN001,
        /// <summary>
        /// Unable to determine a certificate for a file that should be signed
        /// </summary>
        SIGN002,
        /// <summary>
        /// No files to sign.
        /// </summary>
        SIGN003,
        // Signing a 3rd party library with a Microsoft cert.
        SIGN004
    };
        
    internal static class SignToolConstants
    {
        public const string IgnoreFileCertificateSentinel = "None";

        public const string MsiEngineExtension = "-engine.exe";
        /// <summary>
        /// List of known signable extensions. Copied, removing duplicates, from here:
        /// https://microsoft.sharepoint.com/teams/prss/Codesign/SitePages/Signable%20Files.aspx
        /// ".deb" and ".rpm" are not in the list linked above, but they are known signable extension.
        /// </summary>
        public static readonly HashSet<string> SignableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe",
            ".dll",
            ".rll",
            ".olb",
            ".ocx",

            ".cab",

            ".cat",

            ".vbs",
            ".js",
            ".wfs",

            ".msi",
            ".mui",
            ".msp",
            ".msu",
            ".psf",
            ".mpb",
            ".mp",
            ".msm",

            ".doc",
            ".xls",
            ".ppt",
            ".xla",
            ".vdx",
            ".xsn",
            ".mpp",

            ".xlam",
            ".xlsb",
            ".xlsm",
            ".xltm",
            ".potm",
            ".ppsm",
            ".pptm",
            ".docm",
            ".dotm",

            ".ttf",
            ".otf",

            ".ps1",
            ".ps1xml",
            ".psm1",
            ".psd1",
            ".psc1",
            ".cdxml",
            ".wsf",
            ".mof",

            ".sft",
            ".dsft",

            ".vsi",

            ".xap",

            ".efi",

            ".vsix",

            ".jar",

            ".winmd",

            ".appx",
            ".appxbundle",

            ".esd",

            ".py",
            ".pyd",

            ".deb",
            ".pkg",
            ".app",
            ".dylib",
            ".rpm",
        };


        public static readonly HashSet<string> MacSigningOperationsRequiringZipping =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "MacDeveloperHarden",
                    "MacDeveloper",
                    "MacDeveloperVNext",
                    "MacDeveloperVNextHarden",
                    "MacNotarize",
                };

        /// <summary>
        /// Attribute for the CollisionPriorityId
        /// </summary>
        public const string CollisionPriorityId = "CollisionPriorityId";
        
        /// <summary>
        /// Notarization operation microbuild ID. Microbuild does not currently support the friendly name, MacNotarize
        /// </summary>
        public const string MacNotarizationOperation = "8020";
    }
}
