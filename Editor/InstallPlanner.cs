// <copyright project="Enzisoft.Packages.Editor" file="InstallPlanner.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Enzisoft.Packages.Editor
{
    internal enum InstalledSource
    {
        /// <summary>Folder inside Packages/ (development copy). Never touched.</summary>
        Embedded,

        /// <summary>file: dependency on a folder. Never touched.</summary>
        LocalFolder,

        /// <summary>file: dependency on a .tgz archive.</summary>
        LocalTarball,

        Git,

        /// <summary>Resolved from a (scoped) registry; needs that registry whenever Library/ is rebuilt.</summary>
        Registry,

        Other,
    }

    internal sealed class InstalledPackage
    {
        public string Name;
        public string Version;
        public InstalledSource Source;

        /// <summary>The manifest points at Packages/&lt;name&gt;.tgz, i.e. this manager installed it.</summary>
        public bool IsManagedTarball;

        /// <summary>Declared dependencies of the installed version.</summary>
        public string[] Dependencies = Array.Empty<string>();

        /// <summary>Folder copies are the developer's own; the manager leaves them alone.</summary>
        public bool IsProtected => Source is InstalledSource.Embedded or InstalledSource.LocalFolder;
    }

    internal sealed class PlannedPackage
    {
        public PackageVersion Version;

        /// <summary>What it replaces, or null for a new install.</summary>
        public InstalledPackage Replaces;

        /// <summary>The package the user picked rather than a dependency.</summary>
        public bool IsRequested;
    }

    internal sealed class InstallPlan
    {
        public readonly List<PlannedPackage> Packages = new List<PlannedPackage>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Works out which archives an install or update writes: the requested version plus every com.enzisoft.* dependency
    /// the project can't already resolve without the registry. Versions the project already has stay untouched unless a
    /// dependency needs a newer one, so nothing unrelated gets upgraded.
    /// </summary>
    internal static class InstallPlanner
    {
        public const string ManagedScope = "com.enzisoft.";

        public static async Task<InstallPlan> PlanAsync(
            PackageVersion requested, IReadOnlyDictionary<string, InstalledPackage> installed,
            Func<string, CancellationToken, Task<PackageDocument>> getPackage, CancellationToken cancellationToken)
        {
            var plan = new InstallPlan();
            if (installed.TryGetValue(requested.Name, out var current) && current.IsProtected)
                throw new InvalidOperationException($"{requested.Name} is an embedded or local folder package. Update it manually.");

            var planned = new Dictionary<string, PlannedPackage>();
            await AddAsync(requested, true, null);
            plan.Packages.AddRange(planned.Values.OrderBy(p => p.IsRequested).ThenBy(p => p.Version.Name, StringComparer.Ordinal));
            return plan;

            async Task AddAsync(PackageVersion version, bool isRequested, string requiredBy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (planned.TryGetValue(version.Name, out var existing))
                {
                    if (SemVer.Compare(existing.Version.Version, version.Version) >= 0)
                        return;

                    if (existing.IsRequested)
                    {
                        throw new InvalidOperationException(
                            $"{requiredBy} requires {version.Id}, but {existing.Version.Id} was selected.");
                    }
                }

                installed.TryGetValue(version.Name, out var replaces);
                planned[version.Name] = new PlannedPackage { Version = version, Replaces = replaces, IsRequested = isRequested };

                foreach (var dependency in version.Dependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    if (!dependency.Key.StartsWith(ManagedScope, StringComparison.Ordinal))
                        continue;

                    var name = dependency.Key;
                    var required = dependency.Value;
                    if (!SemVer.IsValid(required))
                        throw new InvalidOperationException($"{version.Id} has an unsupported dependency version {name}@{required}.");

                    if (planned.TryGetValue(name, out var alreadyPlanned) && SemVer.Compare(alreadyPlanned.Version.Version, required) >= 0)
                        continue;

                    if (installed.TryGetValue(name, out var local))
                    {
                        var satisfies = SemVer.IsValid(local.Version) && SemVer.Compare(local.Version, required) >= 0;
                        if (local.IsProtected)
                        {
                            if (!satisfies)
                                plan.Warnings.Add($"{version.Id} requires {name}@{required}, but the folder copy is {local.Version}. Update it manually.");
                            continue;
                        }

                        if (satisfies)
                        {
                            if (local.Source != InstalledSource.Registry)
                                continue;

                            // Only a registry can resolve it right now. Vendor the version the project already uses.
                            required = local.Version;
                        }
                    }

                    var document = await getPackage(name, cancellationToken);
                    if (document == null || !document.Versions.TryGetValue(required, out var dependencyVersion))
                        throw new UnauthorizedAccessException($"{version.Id} requires {name}@{required}, which is not available to your account.");

                    await AddAsync(dependencyVersion, false, version.Id);
                }
            }
        }

        /// <summary>Installed packages that declare a dependency on the given package.</summary>
        public static List<string> FindDependents(string name, IEnumerable<InstalledPackage> installed) =>
            installed.Where(p => p.Name != name && p.Dependencies.Contains(name)).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
