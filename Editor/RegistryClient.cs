// <copyright project="Enzisoft.Packages.Editor" file="RegistryClient.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Enzisoft.Packages.Editor
{
    /// <summary>One published version as the registry's package document describes it.</summary>
    internal sealed class PackageVersion
    {
        public string Name;
        public string Version;
        public string DisplayName;
        public string Description;
        public string Author;
        public string Unity;
        public string[] Keywords = Array.Empty<string>();
        public string Integrity;
        public Dictionary<string, string> Dependencies = new Dictionary<string, string>();

        public string Id => $"{Name}@{Version}";
    }

    internal enum PackageAccess
    {
        Free,
        Owned,

        /// <summary>Paid and not bought by this account.</summary>
        None,
    }

    /// <summary>One package of the registry catalog: every package exists here, owned or not.</summary>
    internal sealed class CatalogEntry
    {
        /// <summary>Metadata of the newest release.</summary>
        public PackageVersion Latest;

        public string PublishedAt;
        public PackageAccess Access;

        /// <summary>Newest release the caller may download; older than Latest once their update window ended.</summary>
        public string LatestAccessible;

        /// <summary>Documents bundled in the newest release: readme, changelog, license.</summary>
        public HashSet<string> Docs = new HashSet<string>();

        public string Name => Latest.Name;
    }

    /// <summary>A package document reduced to the versions the caller may access.</summary>
    internal sealed class PackageDocument
    {
        public string Name;

        /// <summary>dist-tags.latest: the newest version the caller may access.</summary>
        public string Latest;

        public Dictionary<string, PackageVersion> Versions = new Dictionary<string, PackageVersion>();

        public PackageVersion LatestVersion => Latest != null && Versions.TryGetValue(Latest, out var version) ? version : null;
    }

    /// <summary>Talks to the Enzisoft registry over the npm protocol with the customer's bearer token.</summary>
    internal sealed class RegistryClient : IDisposable
    {
        public const string DefaultUrl = "https://upm.enzisoft.com";

        public string BaseUrl => _baseUrl;

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public RegistryClient(string baseUrl, string token, HttpMessageHandler handler = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromMinutes(5);
            if (!string.IsNullOrWhiteSpace(token))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        public void Dispose() => _http.Dispose();

        /// <summary>Every package of the registry with the caller's access to it.</summary>
        public async Task<List<CatalogEntry>> GetCatalogAsync(CancellationToken cancellationToken)
        {
            using var response = await _http.GetAsync(_baseUrl + "/-/enzisoft/catalog", cancellationToken);
            EnsureSuccess(response, "the package catalog");
            return ParseCatalog(await response.Content.ReadAsStringAsync());
        }

        /// <summary>Trades the browser sign-in code and its PKCE verifier for a registry token of this Editor.</summary>
        public async Task<string> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
        {
            var body = JsonConvert.SerializeObject(new Dictionary<string, string> { ["code"] = code, ["code_verifier"] = verifier });
            using var response = await _http.PostAsync(_baseUrl + "/editor/token",
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"), cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new UnauthorizedAccessException("The sign-in expired or was already used. Please sign in again.");

            EnsureSuccess(response, "the sign-in");
            return GetString(ParseJson(await response.Content.ReadAsStringAsync()), "token")
                ?? throw new InvalidOperationException("The registry sent no token.");
        }

        /// <summary>Emails of the signed-in account, or null when the token is missing or was revoked.</summary>
        public async Task<List<string>> WhoAmIAsync(CancellationToken cancellationToken)
        {
            using var response = await _http.GetAsync(_baseUrl + "/-/enzisoft/whoami", cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return null;

            EnsureSuccess(response, "the account");
            return ((JArray)ParseJson(await response.Content.ReadAsStringAsync())["emails"]).Select(e => (string)e).ToList();
        }

        /// <summary>Revokes this client's token on the registry (sign out).</summary>
        public async Task RevokeAsync(CancellationToken cancellationToken)
        {
            using var response = await _http.PostAsync(_baseUrl + "/editor/revoke", new StringContent(string.Empty), cancellationToken);
            EnsureSuccess(response, "the sign-out");
        }

        /// <summary>A bundled README, CHANGELOG or LICENSE of a release, or null when the release has none.</summary>
        public async Task<string> GetDocAsync(string name, string version, string kind, CancellationToken cancellationToken)
        {
            ValidateName(name);
            if (!SemVer.IsValid(version))
                throw new InvalidOperationException($"Invalid version '{version}' for {name}.");

            using var response = await _http.GetAsync($"{_baseUrl}/-/enzisoft/docs/{name}/{version}/{Uri.EscapeDataString(kind)}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            EnsureSuccess(response, $"the {kind} of {name}@{version}");
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>The package document, or null when the package doesn't exist or the caller may not access it.</summary>
        public async Task<PackageDocument> GetPackageAsync(string name, CancellationToken cancellationToken)
        {
            ValidateName(name);
            using var response = await _http.GetAsync(_baseUrl + "/" + name, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            EnsureSuccess(response, name);
            return ParsePackage(await response.Content.ReadAsStringAsync());
        }

        /// <summary>Downloads the archive of a version and checks it against the document's sha512 integrity.</summary>
        public async Task<byte[]> DownloadAsync(PackageVersion version, CancellationToken cancellationToken)
        {
            ValidateName(version.Name);
            if (!SemVer.IsValid(version.Version))
                throw new InvalidOperationException($"Invalid version '{version.Version}' for {version.Name}.");

            // Built from the registry URL rather than dist.tarball, so the token is never sent to another host.
            var url = $"{_baseUrl}/{version.Name}/-/{version.Name}-{version.Version}.tgz";
            using var response = await _http.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UnauthorizedAccessException($"{version.Id} is not available to your account.");

            EnsureSuccess(response, version.Id);
            var archive = await response.Content.ReadAsByteArrayAsync();
            Integrity.Verify(archive, version.Integrity, version.Id);
            return archive;
        }

        internal static PackageDocument ParsePackage(string json)
        {
            var root = ParseJson(json);
            var package = new PackageDocument { Name = (string)root["name"] };
            package.Latest = GetString(root["dist-tags"] as JObject, "latest");

            foreach (var entry in ((JObject)root["versions"]).Properties())
            {
                var manifest = (JObject)entry.Value;
                var version = ParseManifest(package.Name, entry.Name, manifest);
                version.Integrity = GetString(manifest["dist"] as JObject, "integrity");
                package.Versions[version.Version] = version;
            }

            return package;
        }

        internal static List<CatalogEntry> ParseCatalog(string json)
        {
            var entries = new List<CatalogEntry>();
            foreach (JObject element in (JArray)ParseJson(json)["packages"])
            {
                var name = GetString(element, "name");
                ValidateName(name);
                var entry = new CatalogEntry
                {
                    Latest = ParseManifest(name, GetString(element, "version"), element),
                    PublishedAt = GetString(element, "publishedAt"),
                    Access = GetString(element, "access") switch
                    {
                        "free" => PackageAccess.Free,
                        "owned" => PackageAccess.Owned,
                        _ => PackageAccess.None,
                    },
                    LatestAccessible = GetString(element, "latestAccessible"),
                };

                if (element["docs"] is JArray docs)
                {
                    foreach (var doc in docs)
                        entry.Docs.Add((string)doc);
                }

                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>Reads package.json fields; the catalog uses the same names, with author already flattened.</summary>
        private static PackageVersion ParseManifest(string name, string version, JObject manifest)
        {
            var parsed = new PackageVersion
            {
                Name = name,
                Version = version,
                DisplayName = GetString(manifest, "displayName"),
                Description = GetString(manifest, "description"),
                Unity = GetString(manifest, "unity"),
                Author = manifest["author"] is JObject author ? GetString(author, "name") : GetString(manifest, "author"),
            };

            if (manifest["keywords"] is JArray keywords)
            {
                parsed.Keywords = keywords
                    .Where(k => k.Type == JTokenType.String)
                    .Select(k => (string)k)
                    .ToArray();
            }

            if (manifest["dependencies"] is JObject dependencies)
            {
                foreach (var dependency in dependencies.Properties())
                    parsed.Dependencies[dependency.Name] = (string)dependency.Value;
            }

            return parsed;
        }

        /// <summary>Package names end up in URLs and file names, so only plain npm names are accepted.</summary>
        internal static void ValidateName(string name)
        {
            var valid = !string.IsNullOrEmpty(name) && name.Length <= 214 && name[0] != '.';
            for (var i = 0; valid && i < name.Length; i++)
            {
                var c = name[i];
                valid = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_';
            }

            if (!valid)
                throw new InvalidOperationException($"Invalid package name '{name}'.");
        }

        /// <summary>Parses a JSON object, keeping date-like strings as strings (Json.NET turns them into DateTime by default).</summary>
        internal static JObject ParseJson(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        private static string GetString(JObject element, string property) =>
            element?[property] is JValue { Type: JTokenType.String } value ? (string)value : null;

        private static void EnsureSuccess(HttpResponseMessage response, string what)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The registry answered {(int)response.StatusCode} {response.ReasonPhrase} for {what}.");
        }
    }
}
