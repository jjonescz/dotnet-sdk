// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.NET.Build.Tasks
{
    public sealed class CreateBuildLoadIsolation : TaskBase
    {
        [Required]
        public ITaskItem[] Items { get; set; }

        [Required]
        public string IsolationRoot { get; set; }

        public string IsolationKind { get; set; } = "default";

        [Output]
        public ITaskItem[] IsolatedItems { get; private set; }

        [Output]
        public string IsolationDirectory { get; private set; }

        protected override void ExecuteCore()
        {
            var existingItems = Items
                .Where(item => !string.IsNullOrEmpty(item.ItemSpec) && File.Exists(item.ItemSpec))
                .Select(item => new IsolationInput(item, Path.GetFullPath(item.ItemSpec), GetDestinationRelativePath(item)))
                .ToArray();

            if (existingItems.Length == 0)
            {
                IsolatedItems = Items ?? Array.Empty<ITaskItem>();
                return;
            }

            string isolationKey = ComputeIsolationKey(existingItems);
            IsolationDirectory = Path.Combine(IsolationRoot, IsolationKind ?? "default", isolationKey);
            Directory.CreateDirectory(IsolationDirectory);

            var isolatedItems = new List<ITaskItem>(Items.Length);
            foreach (ITaskItem item in Items)
            {
                string sourcePath = item.ItemSpec;
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    isolatedItems.Add(item);
                    continue;
                }

                string destinationRelativePath = GetDestinationRelativePath(item);
                string destinationPath = Path.Combine(IsolationDirectory, destinationRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                EnsureMaterialized(sourcePath, destinationPath);

                var isolatedItem = new TaskItem(destinationPath);
                item.CopyMetadataTo(isolatedItem);
                isolatedItem.SetMetadata("OriginalItemSpec", item.ItemSpec);
                isolatedItem.SetMetadata("OriginalFullPath", Path.GetFullPath(item.ItemSpec));
                isolatedItem.SetMetadata("BuildLoadIsolationKey", isolationKey);
                isolatedItem.SetMetadata("BuildLoadIsolationDirectory", IsolationDirectory);
                isolatedItems.Add(isolatedItem);
            }

            IsolatedItems = isolatedItems.ToArray();
        }

        private static string ComputeIsolationKey(IsolationInput[] inputs)
        {
            using SHA256 sha256 = SHA256.Create();
            foreach (IsolationInput input in inputs.OrderBy(input => input.DestinationRelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(input => input.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                Append(sha256, input.DestinationRelativePath);
                Append(sha256, input.SourcePath);
                Append(sha256, GetFileFingerprint(input.SourcePath));
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ToHex(sha256.Hash).Substring(0, 32);
        }

        private static void Append(HashAlgorithm hashAlgorithm, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            hashAlgorithm.TransformBlock(bytes, 0, bytes.Length, null, 0);
            hashAlgorithm.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
        }

        private static string GetFileFingerprint(string path)
        {
            Guid? mvid = TryGetMvid(path);
            if (mvid.HasValue)
            {
                return "mvid:" + mvid.Value.ToString("N");
            }

            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using SHA256 sha256 = SHA256.Create();
            return "sha256:" + ToHex(sha256.ComputeHash(stream));
        }

        private static Guid? TryGetMvid(string path)
        {
            try
            {
                using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using PEReader peReader = new(stream, PEStreamOptions.LeaveOpen);
                if (!peReader.HasMetadata)
                {
                    return null;
                }

                MetadataReader metadataReader = peReader.GetMetadataReader();
                return metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid);
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        private static string GetDestinationRelativePath(ITaskItem item)
        {
            string relativePath = item.GetMetadata("DestinationRelativePath");
            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = item.GetMetadata("TargetPath");
            }

            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = Path.GetFileName(item.ItemSpec);
            }

            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return IsSafeRelativePath(relativePath) ? relativePath : Path.GetFileName(item.ItemSpec);
        }

        private static bool IsSafeRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
            {
                return false;
            }

            return !relativePath.Split(Path.DirectorySeparatorChar).Any(segment => segment == "..");
        }

        private static void EnsureMaterialized(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath) && FilesHaveSameLength(sourcePath, destinationPath))
            {
                return;
            }

            File.Delete(destinationPath);

            if (!TryCreateHardLink(destinationPath, sourcePath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }

        private static bool FilesHaveSameLength(string sourcePath, string destinationPath)
        {
            FileInfo source = new(sourcePath);
            FileInfo destination = new(destinationPath);
            return source.Length == destination.Length;
        }

        private static bool TryCreateHardLink(string destinationPath, string sourcePath)
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                return false;
            }

            return CreateHardLink(destinationPath, sourcePath, IntPtr.Zero);
        }

        private static string ToHex(byte[] bytes)
        {
            char[] result = new char[bytes.Length * 2];
            const string hex = "0123456789abcdef";
            for (int i = 0; i < bytes.Length; i++)
            {
                result[i * 2] = hex[bytes[i] >> 4];
                result[i * 2 + 1] = hex[bytes[i] & 0xF];
            }

            return new string(result);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private sealed class IsolationInput
        {
            public IsolationInput(ITaskItem item, string sourcePath, string destinationRelativePath)
            {
                Item = item;
                SourcePath = sourcePath;
                DestinationRelativePath = destinationRelativePath;
            }

            public ITaskItem Item { get; }

            public string SourcePath { get; }

            public string DestinationRelativePath { get; }
        }
    }
}