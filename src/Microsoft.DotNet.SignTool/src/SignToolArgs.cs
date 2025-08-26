// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.SignTool
{
    internal readonly struct SignToolArgs
    {
        internal string TempDir { get; }
        internal string MicroBuildCorePath { get; }
        internal bool TestSign { get; }
        internal string DotNetToolingPath { get; }
        internal string DotNetMicroBuildPath { get; }
        internal string MSBuildVerbosity { get; }
        internal string SNBinaryPath { get; }
        internal string LogDir { get; }
        internal string EnclosingDir { get; }
        internal string Wix3ToolsPath { get; }
        internal string WixToolsPath { get; }
        internal string TarToolPath { get; }
        internal string PkgToolPath { get; }
        internal int DotNetTimeout { get; }

        internal SignToolArgs(
            string tempPath,
            string microBuildCorePath,
            bool testSign,
            string dotnetToolingPath,
            string DotNetMicroBuildPath,
            string msbuildVerbosity,
            string logDir,
            string enclosingDir,
            string snBinaryPath,
            string wix3ToolsPath,
            string wixToolsPath,
            string tarToolPath,
            string pkgToolPath,
            int dotnetTimeout)
        {
            TempDir = tempPath;
            MicroBuildCorePath = microBuildCorePath;
            TestSign = testSign;
            DotNetToolingPath = dotnetToolingPath;
            DotNetMicroBuildPath = DotNetMicroBuildPath;
            MSBuildVerbosity = msbuildVerbosity;
            LogDir = logDir;
            EnclosingDir = enclosingDir;
            SNBinaryPath = snBinaryPath;
            Wix3ToolsPath = wix3ToolsPath;
            WixToolsPath = wixToolsPath;
            TarToolPath = tarToolPath;
            PkgToolPath = pkgToolPath;
            DotNetTimeout = dotnetTimeout;
        }
    }
}
