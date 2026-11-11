using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Agent.ToolWindow
{
    /// <summary>
    /// Persistent bidirectional WebSocket to the VPS for Visual Studio
    /// "Local Server" mode — the C# analogue of the VS Code WebSocketClient.
    /// Handles auth, chat, and tool_request→local-exec→tool_result routing so
    /// the agent's file/shell tools run on the USER'S machine.
    /// </summary>
    internal sealed class LocalAgentSocket : IDisposable
    {
        private readonly Uri _wsUri;
        private readonly string _token;
        private readonly string _tenant;
        private readonly LocalToolExecutor _executor;
        private readonly Action<JObject> _onEvent;       // forward SSE-equivalent events to the webview

        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private bool _authed;

        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open && _authed;

        public LocalAgentSocket(string apiUrl, string token, string tenant,
            LocalToolExecutor executor, Action<JObject> onEvent)
        {
            // https://host/api/agent → wss://host/api/agent/v1/ws (+ ?tenantDomain=)
            var baseUrl = (apiUrl ?? "").TrimEnd('/');
            baseUrl = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://");
            var url = baseUrl + "/v1/ws";
            if (!string.IsNullOrEmpty(tenant)) url += "?tenantDomain=" + Uri.EscapeDataString(tenant);
            _wsUri = new Uri(url);
            _token = token;
            _tenant = tenant;
            _executor = executor;
            _onEvent = onEvent;
        }

        /// <summary>Connect, send auth, and wait for auth_ok (throws on failure/timeout).</summary>
        public async Task ConnectAsync()
        {
            _ws = new ClientWebSocket();
            if (!string.IsNullOrEmpty(_tenant))
                _ws.Options.SetRequestHeader("X-Tenant-Domain", _tenant);
            _cts = new CancellationTokenSource();

            await _ws.ConnectAsync(_wsUri, CancellationToken.None);
            await SendAsync(new { type = "auth", token = "Bearer " + _token });

            // Receive loop runs in the background; auth is awaited via a TCS.
            _authTcs = new TaskCompletionSource<bool>();
            _ = Task.Run(ReceiveLoop);

            var done = await Task.WhenAny(_authTcs.Task, Task.Delay(15000));
            if (done != _authTcs.Task || !_authTcs.Task.Result)
                throw new Exception("WebSocket auth failed or timed out.");
        }

        private TaskCompletionSource<bool> _authTcs;

        public async Task SendChatAsync(string message, string conversationId, JObject context,
            JToken attachments = null)
        {
            await SendAsync(new
            {
                type = "chat",
                message,
                conversationId,
                attachments,   // [{fileName, mediaType, dataB64}] — null when none
                context,
            });
        }

        public async Task SendChatStopAsync() => await SendAsync(new { type = "stop" });

        private async Task ReceiveLoop()
        {
            var buffer = new byte[16 * 1024];
            var sb = new StringBuilder();
            try
            {
                while (_ws != null && _ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (res.MessageType == WebSocketMessageType.Close)
                        {
                            _authTcs?.TrySetResult(false);
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                    } while (!res.EndOfMessage);

                    JObject data;
                    try { data = JObject.Parse(sb.ToString()); }
                    catch { continue; }
                    await HandleMessage(data);
                }
            }
            catch (OperationCanceledException) { /* disconnect */ }
            catch (Exception e) { Debug.WriteLine($"[Agent] WS recv error: {e.Message}"); }
        }

        private async Task HandleMessage(JObject data)
        {
            switch (data["type"]?.ToString())
            {
                case "auth_ok":
                    _authed = true;
                    _authTcs?.TrySetResult(true);
                    break;

                case "tool_request":
                    await HandleToolRequest(data);
                    break;

                case "event":
                    _onEvent?.Invoke(data);
                    break;

                case "error":
                    if (!_authed) _authTcs?.TrySetResult(false);
                    _onEvent?.Invoke(data);
                    break;

                case "pong":
                    break;
            }
        }

        private async Task HandleToolRequest(JObject data)
        {
            var requestId = data["requestId"]?.ToString();
            var toolName = data["toolName"]?.ToString();
            var toolArgs = data["toolArgs"] as JObject ?? new JObject();
            try
            {
                // File/process tools are synchronous; run off the UI thread.
                var result = await Task.Run(() => _executor.Execute(toolName, toolArgs));
                await SendAsync(new { type = "tool_result", requestId, result });
            }
            catch (Exception e)
            {
                await SendAsync(new { type = "tool_error", requestId, error = e.Message });
            }
        }

        private async Task SendAsync(object payload)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            var json = JsonConvert.SerializeObject(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public void Disconnect()
        {
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client", CancellationToken.None).Wait(2000);
            }
            catch { }
            _authed = false;
            _ws = null;
        }

        public void Dispose() => Disconnect();
    }
}
