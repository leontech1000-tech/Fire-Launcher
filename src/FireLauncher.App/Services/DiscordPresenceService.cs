using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace FireLauncher.Services
{
    internal sealed class DiscordPresenceService : IDisposable
    {
        private ClientWebSocket _socket;
        private bool _enabled;
        private string _applicationId;

        public void Configure(bool enabled, string applicationId)
        {
            _enabled = enabled;
            _applicationId = applicationId == null ? string.Empty : applicationId.Trim();

            if (!_enabled || string.IsNullOrWhiteSpace(_applicationId))
            {
                Disconnect();
            }
        }

        public void UpdatePresence(string details, string state)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(_applicationId))
            {
                return;
            }

            try
            {
                EnsureConnected();
                if (_socket == null || _socket.State != WebSocketState.Open)
                {
                    return;
                }

                var payload =
                    "{" +
                    "\"cmd\":\"SET_ACTIVITY\"," +
                    "\"args\":{" +
                    "\"pid\":" + Process.GetCurrentProcess().Id + "," +
                    "\"activity\":{" +
                    "\"details\":\"" + Escape(details) + "\"," +
                    "\"state\":\"" + Escape(state) + "\"" +
                    "}" +
                    "}," +
                    "\"nonce\":\"" + Guid.NewGuid().ToString("N") + "\"" +
                    "}";

                var bytes = Encoding.UTF8.GetBytes(payload);
                var segment = new ArraySegment<byte>(bytes);
                _socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                Disconnect();
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void EnsureConnected()
        {
            if (_socket != null && _socket.State == WebSocketState.Open)
            {
                return;
            }

            Disconnect();

            for (var port = 6463; port <= 6472; port++)
            {
                var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Origin", "https://localhost");

                try
                {
                    var uri = new Uri(
                        "ws://127.0.0.1:" + port +
                        "/?v=1&client_id=" + Uri.EscapeDataString(_applicationId) +
                        "&encoding=json");

                    socket.ConnectAsync(uri, CancellationToken.None).GetAwaiter().GetResult();
                    _socket = socket;
                    DrainReadyMessage();
                    return;
                }
                catch
                {
                    socket.Dispose();
                }
            }
        }

        private void DrainReadyMessage()
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
            {
                return;
            }

            var buffer = new byte[8192];
            var segment = new ArraySegment<byte>(buffer);

            try
            {
                using (var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
                {
                    _socket.ReceiveAsync(segment, cancellationSource.Token).GetAwaiter().GetResult();
                }
            }
            catch
            {
                // Ignore Discord RPC welcome payload failures and allow later retry.
            }
        }

        private void Disconnect()
        {
            if (_socket == null)
            {
                return;
            }

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch
            {
            }
            finally
            {
                _socket.Dispose();
                _socket = null;
            }
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }
    }
}
