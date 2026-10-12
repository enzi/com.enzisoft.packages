// <copyright project="Enzisoft.Packages.Editor" file="Integrity.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Security.Cryptography;

namespace Enzisoft.Packages.Editor
{
    internal static class Integrity
    {
        private const string Sha512Prefix = "sha512-";

        /// <summary>
        /// Throws unless the archive matches the npm <c>dist.integrity</c> sha512 value. A missing hash is an error, since an
        /// unchecked archive would be committed into the customer's project.
        /// </summary>
        public static void Verify(byte[] archive, string integrity, string packageId)
        {
            string expectedBase64 = null;
            foreach (var token in (integrity ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith(Sha512Prefix, StringComparison.Ordinal))
                    expectedBase64 = token.Substring(Sha512Prefix.Length);
            }

            if (expectedBase64 == null)
                throw new InvalidDataException($"The registry sent no sha512 integrity hash for {packageId}.");

            byte[] expected;
            try
            {
                expected = Convert.FromBase64String(expectedBase64);
            }
            catch (FormatException e)
            {
                throw new InvalidDataException($"The registry sent an invalid integrity hash for {packageId}.", e);
            }

            byte[] actual;
            using (var sha512 = SHA512.Create())
                actual = sha512.ComputeHash(archive);

            if (!AreEqual(expected, actual))
                throw new InvalidDataException($"The download of {packageId} is corrupted (sha512 mismatch). Try again.");
        }

        private static bool AreEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }
    }
}
