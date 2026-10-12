// <copyright project="Enzisoft.Packages.Editor" file="PackageAction.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

namespace Enzisoft.Packages.Editor
{
    internal enum PackageActionKind
    {
        None,
        Install,
        Update,
        Downgrade,

        /// <summary>Same version, but resolved from git or a registry: switch it to a committed Packages/&lt;name&gt;.tgz.</summary>
        Vendor,
    }

    /// <summary>What installing a selected version would do to the project's copy of the package.</summary>
    internal readonly struct PackageAction
    {
        public readonly PackageActionKind Kind;
        public readonly string Label;

        private PackageAction(PackageActionKind kind, string label)
        {
            Kind = kind;
            Label = label;
        }

        public static PackageAction For(string version, InstalledPackage local)
        {
            if (local == null)
                return new PackageAction(PackageActionKind.Install, $"Install {version}");

            if (local.IsProtected)
                return new PackageAction(PackageActionKind.None, "Folder copy");

            if (!SemVer.IsValid(local.Version))
                return new PackageAction(PackageActionKind.Update, $"Replace with {version}");

            var comparison = SemVer.Compare(version, local.Version);
            if (comparison > 0)
                return new PackageAction(PackageActionKind.Update, $"Update to {version}");
            if (comparison < 0)
                return new PackageAction(PackageActionKind.Downgrade, $"Downgrade to {version}");

            return local.IsManagedTarball
                ? new PackageAction(PackageActionKind.None, "Installed")
                : new PackageAction(PackageActionKind.Vendor, "Switch to .tgz");
        }
    }
}
