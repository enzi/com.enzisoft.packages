// <copyright project="Enzisoft.Packages.Editor" file="ProjectPackages.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Enzisoft.Packages.Editor
{
    /// <summary>Reads what the open project has installed and applies install plans through Unity's Package Manager.</summary>
    internal static class ProjectPackages
    {
        public static string PackagesDir => Path.GetFullPath("Packages");

        private static string WorkDir => Path.GetFullPath(Path.Combine("Temp", "EnzisoftPackages"));

        /// <summary>Every registered package by name, so dependents of any package can be found.</summary>
        public static Dictionary<string, InstalledPackage> Read()
        {
            var manifestDependencies = ReadManifestDependencies(Path.Combine(PackagesDir, "manifest.json"));
            var installed = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
            foreach (var info in PackageInfo.GetAllRegisteredPackages())
            {
                installed[info.name] = new InstalledPackage
                {
                    Name = info.name,
                    Version = info.version,
                    Source = ToSource(info.source),
                    IsManagedTarball = info.source == PackageSource.LocalTarball
                        && manifestDependencies.TryGetValue(info.name, out var dependency)
                        && dependency == TarballFiles.Dependency(info.name),
                    Dependencies = info.dependencies.Select(d => d.name).ToArray(),
                };
            }

            return installed;
        }

        public static async Task InstallAsync(
            InstallPlan plan, RegistryClient client, Action<string, float> progress, CancellationToken cancellationToken)
        {
            var archives = new List<(string Name, byte[] Archive)>();
            for (var i = 0; i < plan.Packages.Count; i++)
            {
                var version = plan.Packages[i].Version;
                progress($"Downloading {version.Id}...", (float)i / plan.Packages.Count);
                archives.Add((version.Name, await client.DownloadAsync(version, cancellationToken)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress("Adding packages to the project...", 1);

            var files = new TarballFiles(PackagesDir, WorkDir);
            files.Apply(archives);

            // Unity writes the file: dependencies into manifest.json and replaces git or registry entries of the same name.
            var request = Client.AddAndRemove(archives.Select(a => TarballFiles.Dependency(a.Name)).ToArray());
            try
            {
                await WaitAsync(request);
            }
            catch
            {
                files.Rollback();
                throw;
            }

            files.Commit();
        }

        public static async Task RemoveAsync(string name)
        {
            var installed = Read();
            if (!installed.TryGetValue(name, out var package) || !package.IsManagedTarball)
                throw new InvalidOperationException($"{name} was not installed by Enzisoft Packages, so it isn't removed here.");

            var dependents = InstallPlanner.FindDependents(name, installed.Values);
            if (dependents.Count > 0)
                throw new InvalidOperationException($"{name} is still required by {string.Join(", ", dependents)}.");

            await WaitAsync(Client.Remove(name));

            var archive = Path.Combine(PackagesDir, TarballFiles.FileName(name));
            if (File.Exists(archive))
                File.Delete(archive);
        }

        private static async Task WaitAsync(Request request)
        {
            while (!request.IsCompleted)
                await Task.Delay(100);

            if (request.Status != StatusCode.Success)
                throw new InvalidOperationException("Unity Package Manager: " + (request.Error?.message ?? "the request failed."));
        }

        private static Dictionary<string, string> ReadManifestDependencies(string manifestPath)
        {
            var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            // Json.NET skips comments and accepts trailing commas, as Unity does in manifest.json.
            if (RegistryClient.ParseJson(File.ReadAllText(manifestPath))["dependencies"] is JObject element)
            {
                foreach (var dependency in element.Properties())
                {
                    if (dependency.Value.Type == JTokenType.String)
                        dependencies[dependency.Name] = (string)dependency.Value;
                }
            }

            return dependencies;
        }

        private static InstalledSource ToSource(PackageSource source) => source switch
        {
            PackageSource.Embedded => InstalledSource.Embedded,
            PackageSource.Local => InstalledSource.LocalFolder,
            PackageSource.LocalTarball => InstalledSource.LocalTarball,
            PackageSource.Git => InstalledSource.Git,
            PackageSource.Registry => InstalledSource.Registry,
            _ => InstalledSource.Other,
        };
    }
}
