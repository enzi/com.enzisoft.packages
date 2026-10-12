// <copyright project="Enzisoft.Packages.Editor" file="BrowserSignIn.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Enzisoft.Packages.Editor
{
    /// <summary>
    /// Browser sign-in for a public client (OAuth for native apps, RFC 8252, with PKCE): the registry sends a one-time
    /// code back to a listener on 127.0.0.1, and only this Editor knows the verifier that turns it into a token.
    /// </summary>
    internal static class Pkce
    {
        public static string NewVerifier() => Base64Url(RandomBytes(32));

        public static string NewState() => Base64Url(RandomBytes(16));

        /// <summary>S256: base64url(SHA-256(verifier)).</summary>
        public static string Challenge(string verifier)
        {
            using var sha = SHA256.Create();
            return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            return bytes;
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Waits on a random loopback port for the browser to deliver the sign-in code.</summary>
    internal sealed class LoopbackCallback : IDisposable
    {
        private readonly TcpListener _listener;

        public LoopbackCallback()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public void Dispose() => _listener.Stop();

        /// <summary>The code from the first /callback request; throws if its state isn't the one this sign-in sent.</summary>
        public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(_listener.Stop))
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception e) when (cancellationToken.IsCancellationRequested && e is ObjectDisposedException or SocketException or InvalidOperationException)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    using (client)
                    {
                        var stream = client.GetStream();
                        var requestLine = await new StreamReader(stream, Encoding.ASCII).ReadLineAsync() ?? string.Empty;

                        // "GET /callback?code=...&state=... HTTP/1.1"; anything else (e.g. /favicon.ico) is ignored.
                        var parts = requestLine.Split(' ');
                        var target = parts.Length >= 2 ? parts[1] : string.Empty;
                        if (!target.StartsWith("/callback?", StringComparison.Ordinal))
                        {
                            await RespondAsync(stream, "404 Not Found", "Not found.");
                            continue;
                        }

                        var code = QueryValue(target, "code");
                        if (QueryValue(target, "state") != expectedState || string.IsNullOrEmpty(code))
                        {
                            await RespondAsync(stream, "400 Bad Request", "This sign-in doesn't match the one Unity started. Start it again from the Enzisoft Packages window.");
                            throw new InvalidOperationException("The browser returned an unexpected sign-in response. Please try again.");
                        }

                        await RespondAsync(stream, "200 OK", "Signed in. You can close this tab and return to Unity.");
                        return code;
                    }
                }
            }
        }

        internal static string QueryValue(string target, string name)
        {
            var query = target.IndexOf('?') is var index && index >= 0 ? target.Substring(index + 1) : string.Empty;
            foreach (var pair in query.Split('&'))
            {
                var separator = pair.IndexOf('=');
                if (separator > 0 && pair.Substring(0, separator) == name)
                    return Uri.UnescapeDataString(pair.Substring(separator + 1).Replace('+', ' '));
            }

            return null;
        }

        private static async Task RespondAsync(Stream stream, string status, string message)
        {
            var body = Encoding.UTF8.GetBytes(
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>Enzisoft Packages</title></head>" +
                $"<body style=\"font-family:system-ui,sans-serif;margin:3rem\"><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length);
            await stream.WriteAsync(body, 0, body.Length);
        }
    }
}
