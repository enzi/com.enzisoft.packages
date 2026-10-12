// <copyright project="Enzisoft.Packages.Editor" file="SemVer.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System;

namespace Enzisoft.Packages.Editor
{
    /// <summary>Semantic version ordering (major.minor.patch[-prerelease][+build]) as npm and UPM use it.</summary>
    internal static class SemVer
    {
        public static bool IsValid(string version) => TryParse(version, out _, out _);

        /// <summary>Negative if a is older than b, 0 if equal, positive if newer. Throws on invalid versions.</summary>
        public static int Compare(string a, string b)
        {
            if (!TryParse(a, out var coreA, out var preA))
                throw new FormatException($"'{a}' is not a valid version.");
            if (!TryParse(b, out var coreB, out var preB))
                throw new FormatException($"'{b}' is not a valid version.");

            for (var i = 0; i < 3; i++)
            {
                var result = coreA[i].CompareTo(coreB[i]);
                if (result != 0)
                    return result;
            }

            // A release ranks above its prereleases.
            if (preA == null || preB == null)
                return preA == null ? (preB == null ? 0 : 1) : -1;

            var idsA = preA.Split('.');
            var idsB = preB.Split('.');
            for (var i = 0; i < Math.Min(idsA.Length, idsB.Length); i++)
            {
                var numericA = long.TryParse(idsA[i], out var numberA);
                var numericB = long.TryParse(idsB[i], out var numberB);
                var result = numericA && numericB
                    ? numberA.CompareTo(numberB)
                    : numericA != numericB
                        ? (numericA ? -1 : 1)
                        : string.CompareOrdinal(idsA[i], idsB[i]);
                if (result != 0)
                    return Math.Sign(result);
            }

            return idsA.Length.CompareTo(idsB.Length);
        }

        private static bool TryParse(string version, out long[] core, out string prerelease)
        {
            core = new long[3];
            prerelease = null;
            if (string.IsNullOrEmpty(version))
                return false;

            var plus = version.IndexOf('+');
            if (plus >= 0)
                version = version.Substring(0, plus);

            var dash = version.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = version.Substring(dash + 1);
                version = version.Substring(0, dash);
                if (prerelease.Length == 0)
                    return false;
            }

            var parts = version.Split('.');
            if (parts.Length != 3)
                return false;

            for (var i = 0; i < 3; i++)
            {
                if (parts[i].Length == 0 || !long.TryParse(parts[i], System.Globalization.NumberStyles.None, null, out core[i]))
                    return false;
            }

            return true;
        }
    }
}
