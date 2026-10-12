// <copyright project="Enzisoft.Packages.Editor" file="TarballFiles.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;

namespace Enzisoft.Packages.Editor
{
    /// <summary>
    /// Swaps Packages/&lt;name&gt;.tgz archives in as one step that can be undone: new archives are written to a work folder
    /// first, existing ones are moved aside, and <see cref="Rollback"/> puts them back if Unity rejects the change.
    /// </summary>
    internal sealed class TarballFiles
    {
        private readonly string _packagesDir;
        private readonly string _workDir;
        private readonly List<(string Target, string Backup)> _replaced = new List<(string, string)>();

        public TarballFiles(string packagesDir, string workDir)
        {
            _packagesDir = packagesDir;
            _workDir = workDir;
        }

        public static string FileName(string packageName) => packageName + ".tgz";

        /// <summary>The manifest dependency value for a managed archive, relative to Packages/.</summary>
        public static string Dependency(string packageName) => "file:" + FileName(packageName);

        /// <summary>Writes every archive into Packages/. On any failure, already swapped archives are restored.</summary>
        public void Apply(IReadOnlyList<(string Name, byte[] Archive)> archives)
        {
            if (Directory.Exists(_workDir))
                Directory.Delete(_workDir, true);
            Directory.CreateDirectory(_workDir);

            var staged = new List<(string Name, string Path)>();
            foreach (var (name, archive) in archives)
            {
                RegistryClient.ValidateName(name);
                var path = Path.Combine(_workDir, FileName(name));
                File.WriteAllBytes(path, archive);
                staged.Add((name, path));
            }

            try
            {
                foreach (var (name, path) in staged)
                {
                    var target = Path.Combine(_packagesDir, FileName(name));
                    string backup = null;
                    if (File.Exists(target))
                    {
                        backup = Path.Combine(_workDir, FileName(name) + ".backup");
                        File.Move(target, backup);
                    }

                    _replaced.Add((target, backup));
                    File.Move(path, target);
                }
            }
            catch
            {
                Rollback();
                throw;
            }
        }

        /// <summary>Restores the archives that were in Packages/ before <see cref="Apply"/>.</summary>
        public void Rollback()
        {
            for (var i = _replaced.Count - 1; i >= 0; i--)
            {
                var (target, backup) = _replaced[i];
                if (File.Exists(target))
                    File.Delete(target);
                if (backup != null && File.Exists(backup))
                    File.Move(backup, target);
            }

            _replaced.Clear();
        }

        /// <summary>Drops the backups once Unity accepted the change.</summary>
        public void Commit()
        {
            _replaced.Clear();
            try
            {
                if (Directory.Exists(_workDir))
                    Directory.Delete(_workDir, true);
            }
            catch (IOException)
            {
                // Temp/ is cleared when the Editor closes anyway.
            }
        }
    }
}
