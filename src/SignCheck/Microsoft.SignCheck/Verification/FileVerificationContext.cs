// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;

namespace Microsoft.SignCheck.Verification
{
    public class FileVerificationContext
    {
        public string Path { get; }
        public string Parent { get; }
        public string VirtualPath { get; }
        public string ContainerPath { get; }

        public FileVerificationContext(string path, string parent, string virtualPath, string containerPath)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException(nameof(path));
            }

            Path = path;
            Parent = parent;
            VirtualPath = virtualPath;
            ContainerPath = containerPath;
        }

        public static FileVerificationContext CreateTopLevel(string path)
            => new FileVerificationContext(path, parent: null, virtualPath: System.IO.Path.GetFileName(path), containerPath: null);
    }
}
