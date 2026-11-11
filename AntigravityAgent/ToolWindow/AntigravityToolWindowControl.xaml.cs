using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Agent.ToolWindow
{
    /// <summary>
    /// VS extension tool-window control — production-grade agent chat surface.
    ///
    /// Feature parity with the VS Code extension:
    ///   • SSE streaming with all event types (message.delta, tool.start/end,
    ///     plan, citations, approval.request, message.end with cost/token meter)
    ///   • HITL approval flow (webview → C# → HTTP POST)
    ///   • Token refresh on 401 (single-flighted)
    ///   • Configurable API URL (settings-backed, defaults to cloud)
    /// </summary>
    public partial class AgentToolWindowControl : UserControl
    {
        private static readonly HttpClient httpClient = new HttpClient();

        private string authToken = string.Empty;
        private string refreshToken = string.Empty;

        // Single-flight guard: prevent concurrent refresh-token calls.
        private SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        // Cancellation for the active SSE stream (so the user can stop mid-turn).
        private CancellationTokenSource _streamCts;

        // Baked-in per-tenant config (apiUrl + tenantDomain), shipped inside the
        // VSIX as agent.tenant.json next to the DLL and rewritten per-tenant at
        // download time by the API (mirrors the VS Code .vsix package.json bake).
        // This is what makes a fresh install pre-pointed at PROD + the right
        // tenant with zero manual setup — exactly like the VS Code extension.
        private JObject _bakedConfig;
        private JObject BakedConfig
        {
            get
            {
                if (_bakedConfig != null) return _bakedConfig;
                _bakedConfig = new JObject();
                try
                {
                    var path = Path.Combine(
                        Path.GetDirectoryName(GetType().Assembly.Location),
                        "agent.tenant.json");
                    if (File.Exists(path))
                        _bakedConfig = JObject.Parse(File.ReadAllText(path));
                }
                catch (Exception ex) { Debug.WriteLine($"[Agent] baked config read failed: {ex.Message}"); }
                return _bakedConfig;
            }
        }

        private void WriteVsSetting(string name, string value)
        {
            try
            {
                var shell = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                    .GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsSettingsManager));
                if (shell is Microsoft.VisualStudio.Shell.Interop.IVsSettingsManager mgr)
                {
                    mgr.GetWritableSettingsStore(
                        (uint)Microsoft.VisualStudio.Shell.Interop.__VsSettingsScope.SettingsScope_UserSettings,
                        out var store);
                    if (store != null)
                    {
                        store.CollectionExists("Agent", out var exists);
                        if (exists == 0) store.CreateCollection("Agent");
                        store.SetString("Agent", name, value);
                    }
                }
            }
            catch { /* settings unavailable — keep in-memory only */ }
        }

        private string ReadVsSetting(string name)
        {
            try
            {
                var shell = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                    .GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsSettingsManager));
                if (shell is Microsoft.VisualStudio.Shell.Interop.IVsSettingsManager mgr)
                {
                    mgr.GetReadOnlySettingsStore(
                        (uint)Microsoft.VisualStudio.Shell.Interop.__VsSettingsScope.SettingsScope_UserSettings,
                        out var store);
                    if (store != null)
                    {
                        store.GetString("Agent", name, out var val);
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }
            catch { /* settings unavailable */ }
            return null;
        }

        /// <summary>
        /// Resolve the API base URL.  Order:
        ///   1. VS settings (WritableSettingsStore → "Agent/ApiUrl") — user override
        ///   2. Environment variable AG_API_URL
        ///   3. Baked-in agent.tenant.json (set per-tenant at download)
        ///   4. PROD gateway default (same path the VS Code extension uses)
        /// </summary>
        private string ApiUrl
        {
            get
            {
                var fromSettings = ReadVsSetting("ApiUrl");
                if (!string.IsNullOrWhiteSpace(fromSettings)) return fromSettings.TrimEnd('/');

                var env = Environment.GetEnvironmentVariable("AG_API_URL");
                if (!string.IsNullOrWhiteSpace(env)) return env.TrimEnd('/');

                var baked = BakedConfig["apiUrl"]?.ToString();
                if (!string.IsNullOrWhiteSpace(baked)) return baked.TrimEnd('/');

                // PROD gateway (matches VS Code agent.apiUrl default).
                return "https://ai.storeglance.com/api";
            }
        }

        /// <summary>
        /// The tenant domain for this install (multi-tenant routing + branding).
        /// Sent as X-Tenant-Domain so the API routes to the right tenant, exactly
        /// like the VS Code extension's agent.tenantDomain setting.  Order:
        ///   1. VS settings (Agent/TenantDomain)  2. AG_TENANT_DOMAIN env
        ///   3. Baked-in agent.tenant.json (set per-tenant at download)
        /// </summary>
        private string TenantDomain
        {
            get
            {
                var fromSettings = ReadVsSetting("TenantDomain");
                if (!string.IsNullOrWhiteSpace(fromSettings))
                    return fromSettings.Split(':')[0].Trim().ToLowerInvariant();

                var env = Environment.GetEnvironmentVariable("AG_TENANT_DOMAIN");
                if (!string.IsNullOrWhiteSpace(env))
                    return env.Split(':')[0].Trim().ToLowerInvariant();

                var baked = BakedConfig["tenantDomain"]?.ToString();
                if (!string.IsNullOrWhiteSpace(baked))
                    return baked.Split(':')[0].Trim().ToLowerInvariant();

                return string.Empty;
            }
        }

        /// <summary>Add the tenant header so every API call is tenant-scoped.</summary>
        private void AddTenantHeader(System.Net.Http.Headers.HttpHeaders headers)
        {
            var domain = TenantDomain;
            if (!string.IsNullOrEmpty(domain))
                headers.Add("X-Tenant-Domain", domain);
        }

        // ── Workspace mode (Local Server / Cloud Server) ─────────────────
        // 'local' = file/shell tools run on the USER'S machine via WebSocket
        // (mirrors the VS Code extension). 'cloud' = everything runs on the VPS.
        private string _workspaceMode = "local";
        private LocalAgentSocket _localSocket;

        private string ReadWorkspaceMode()
        {
            var v = ReadVsSetting("WorkspaceMode");
            return (v == "cloud" || v == "local") ? v : "local";
        }

        public AgentToolWindowControl()
        {
            InitializeComponent();
            _workspaceMode = ReadWorkspaceMode();
            InitializeAsync();
        }

        // ── WebView2 initialisation ─────────────────────────────────────

        private async void InitializeAsync()
        {
            try
            {
                var userDataFolder = Path.Combine(
                    Path.GetTempPath(), "AgentWebView");

                var env = await CoreWebView2Environment.CreateAsync(
                    null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.WebMessageReceived += OnWebMessage;

                // Push per-tenant branding once the page has loaded (so the
                // webview's message listener is registered). Mirrors the VS Code
                // extension's _loadBranding → 'branding' message.
                webView.CoreWebView2.NavigationCompleted += async (s, e) => await LoadBranding();

                // Production: map a virtual host to the bundled webview-ui dist.
                // The VSIX build step should copy `webview-ui/dist` into the
                // extension output so it ships alongside the DLL.
                var distFolder = Path.Combine(
                    Path.GetDirectoryName(GetType().Assembly.Location),
                    "webview-ui", "dist");

                if (Directory.Exists(distFolder))
                {
                    webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "agent.local", distFolder,
                        CoreWebView2HostResourceAccessKind.Allow);
                    webView.CoreWebView2.Navigate("https://agent.local/index.html");
                }
                else
                {
                    // Dev fallback: Vite / Angular dev server
                    webView.CoreWebView2.Navigate("http://localhost:4200");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Agent] WebView2 init failed: {ex.Message}");
            }
        }

        // ── Per-tenant branding ─────────────────────────────────────────

        private bool _brandingLoaded = false;

        /// <summary>
        /// Fetch /TenantConfig for the configured tenant and forward branding
        /// (org name, logo, theme colour) to the webview. Best-effort and
        /// idempotent; on any failure the UI keeps its neutral defaults.
        /// </summary>
        private async Task LoadBranding()
        {
            if (_brandingLoaded) return;
            try
            {
                var domain = TenantDomain;
                if (string.IsNullOrWhiteSpace(domain)) return;

                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{ApiUrl}/TenantConfig?subdomain={Uri.EscapeDataString(domain)}");
                AddTenantHeader(request.Headers);

                var response = await httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return;

                var cfg = JObject.Parse(
                    await response.Content.ReadAsStringAsync());

                _brandingLoaded = true;
                PostWebMessage(new
                {
                    type = "branding",
                    orgName = cfg["orgName"]?.ToString() ?? "",
                    logoUrl = cfg["logoUrl"]?.ToString() ?? "",
                    themeColor = cfg["themeColor"]?.ToString() ?? "",
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Agent] LoadBranding failed: {ex.Message}");
            }
        }

        // ── Inbound messages from the webview ───────────────────────────

        private async void OnWebMessage(
            object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // The webview posts OBJECTS via chrome.webview.postMessage({...}).
                // TryGetWebMessageAsString() only returns a value when the page
                // posted a *string*, so for our object messages it is null and we
                // must read WebMessageAsJson — otherwise every inbound message
                // (login_request, user_input, …) is silently dropped.
                string raw = null;
                try { raw = e.TryGetWebMessageAsString(); } catch { /* not a string */ }
                if (string.IsNullOrWhiteSpace(raw))
                {
                    try { raw = e.WebMessageAsJson; } catch { /* unavailable */ }
                }
                if (string.IsNullOrWhiteSpace(raw)) return;

                var message = JObject.Parse(raw);
                var type = message["type"]?.ToString();

                switch (type)
                {
                    case "webview_ready":
                        // The webview has mounted and registered its message
                        // listener. Drive initial state now (no page-load race):
                        // gate on auth, then push branding. Without this the
                        // webview keeps its default "authenticated" state and
                        // never shows the login screen.
                        if (string.IsNullOrEmpty(authToken))
                            PostWebMessage(new { type = "require_login" });
                        else
                            PostWebMessage(new { type = "login_success" });
                        _brandingLoaded = false;
                        await LoadBranding();
                        // Sync the execution-mode toggle with the host's state.
                        PostWebMessage(new { type = "mode_changed", mode = _workspaceMode });
                        break;

                    case "switch_mode":
                        {
                            var newMode = message["mode"]?.ToString() == "cloud" ? "cloud" : "local";
                            if (newMode != _workspaceMode)
                            {
                                if (_workspaceMode == "local") { _localSocket?.Dispose(); _localSocket = null; }
                                _workspaceMode = newMode;
                                WriteVsSetting("WorkspaceMode", newMode);
                            }
                            PostWebMessage(new { type = "mode_changed", mode = _workspaceMode });
                        }
                        break;

                    case "login_request":
                        await HandleLogin(
                            message["username"]?.ToString(),
                            message["password"]?.ToString());
                        break;

                    case "logout_request":
                        authToken = string.Empty;
                        refreshToken = string.Empty;
                        PostWebMessage(new { type = "require_login" });
                        break;

                    case "user_input":
                        await HandleUserInput(
                            message["text"]?.ToString(),
                            message["conversationId"]?.ToString(),
                            message["attachments"]);
                        break;

                    case "load_conversations":
                        await HandleLoadConversations();
                        break;

                    case "load_conversation":
                        await HandleLoadConversation(
                            message["conversationId"]?.ToString());
                        break;

                    case "delete_conversation":
                        await HandleDeleteConversation(
                            message["conversationId"]?.ToString());
                        break;

                    case "new_chat":
                        _streamCts?.Cancel();
                        PostWebMessage(new { type = "new_chat_ready" });
                        break;

                    case "approval_response":
                        await HandleApprovalResponse(
                            message["approvalId"]?.ToString(),
                            message["approved"]?.Value<bool>() ?? false,
                            message["reason"]?.ToString());
                        break;

                    case "stop_generation":
                        _streamCts?.Cancel();
                        if (_localSocket != null) { try { await _localSocket.SendChatStopAsync(); } catch { } }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Agent] WebMessage error: {ex.Message}");
            }
        }

        // ── Outbound helper ─────────────────────────────────────────────

        private void PostWebMessage(object message)
        {
            try
            {
                var json = JsonConvert.SerializeObject(message);
                webView.Dispatcher.Invoke(() =>
                    webView.CoreWebView2?.PostWebMessageAsJson(json));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Agent] PostWebMessage failed: {ex.Message}");
            }
        }

        // ── Authentication ──────────────────────────────────────────────

        private async Task HandleLogin(string username, string password)
        {
            try
            {
                var payload = JsonConvert.SerializeObject(new
                {
                    grantType = "client_credentials",
                    userName = username,
                    password = password,
                });
                var content = new StringContent(
                    payload, Encoding.UTF8, "application/json");
                content.Headers.Add("X-AG-Source", "visualstudio");
                AddTenantHeader(content.Headers);

                var response = await httpClient.PostAsync(
                    $"{ApiUrl}/Login", content);

                if (response.IsSuccessStatusCode)
                {
                    var result = JObject.Parse(
                        await response.Content.ReadAsStringAsync());

                    if (result["isAuthenticated"]?.Value<bool>() == true
                        && result["accessToken"] != null)
                    {
                        authToken = result["accessToken"].ToString();
                        refreshToken = result["refreshToken"]?.ToString()
                                       ?? string.Empty;
                        PostWebMessage(new { type = "login_success" });
                        return;
                    }

                    var msg = result["message"]?.ToString()
                              ?? "Login failed.";
                    PostWebMessage(new { type = "login_failed", error = msg });
                }
                else
                {
                    string detail = $"Server error ({(int)response.StatusCode})";
                    try
                    {
                        var body = JObject.Parse(
                            await response.Content.ReadAsStringAsync());
                        detail = body["detail"]?.ToString()
                                 ?? body["message"]?.ToString()
                                 ?? detail;
                    }
                    catch { /* non-JSON error body */ }

                    PostWebMessage(new { type = "login_failed", error = detail });
                }
            }
            catch (Exception ex)
            {
                PostWebMessage(new { type = "login_failed", error = ex.Message });
            }
        }

        /// <summary>
        /// Attempt to renew the access token using the stored refresh token.
        /// Single-flighted: concurrent 401 retries share one refresh call.
        /// Returns the new token, or null (caller should force re-login).
        /// </summary>
        private async Task<string> TryRefreshToken()
        {
            if (string.IsNullOrEmpty(authToken) ||
                string.IsNullOrEmpty(refreshToken))
                return null;

            await _refreshLock.WaitAsync();
            try
            {
                var payload = JsonConvert.SerializeObject(new
                {
                    grantType = "refresh_token",
                    token = new { token = authToken, refreshToken = refreshToken },
                });
                var content = new StringContent(
                    payload, Encoding.UTF8, "application/json");
                content.Headers.Add("X-AG-Source", "visualstudio");
                AddTenantHeader(content.Headers);

                var response = await httpClient.PostAsync(
                    $"{ApiUrl}/Login", content);

                if (!response.IsSuccessStatusCode) return null;

                var result = JObject.Parse(
                    await response.Content.ReadAsStringAsync());

                if (result["isAuthenticated"]?.Value<bool>() == true
                    && result["accessToken"] != null)
                {
                    authToken = result["accessToken"].ToString();
                    if (result["refreshToken"] != null)
                        refreshToken = result["refreshToken"].ToString();
                    return authToken;
                }
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>
        /// Authenticated HTTP request with transparent 401 → refresh → retry.
        /// Returns null when the user must re-authenticate.
        /// </summary>
        private async Task<HttpResponseMessage> AuthedRequest(
            HttpMethod method, string path,
            HttpContent body = null, bool retried = false)
        {
            if (string.IsNullOrEmpty(authToken))
            {
                PostWebMessage(new { type = "require_login" });
                return null;
            }

            var request = new HttpRequestMessage(method, $"{ApiUrl}{path}");
            request.Headers.Add("Authorization", $"Bearer {authToken}");
            request.Headers.Add("X-AG-Source", "visualstudio");
            AddTenantHeader(request.Headers);
            if (body != null) request.Content = body;

            var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead);

            if (response.StatusCode ==
                System.Net.HttpStatusCode.Unauthorized && !retried)
            {
                var newToken = await TryRefreshToken();
                if (newToken != null)
                    return await AuthedRequest(method, path, body, true);

                authToken = string.Empty;
                refreshToken = string.Empty;
                PostWebMessage(new { type = "require_login" });
                return null;
            }

            return response;
        }

        // ── Conversation management ─────────────────────────────────────

        private async Task HandleLoadConversations()
        {
            try
            {
                var response = await AuthedRequest(
                    HttpMethod.Get, "/v1/chat/conversations");
                if (response == null) return;

                if (response.IsSuccessStatusCode)
                {
                    var data = JArray.Parse(
                        await response.Content.ReadAsStringAsync());
                    PostWebMessage(new
                    {
                        type = "conversations_loaded",
                        conversations = data,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[Agent] Load conversations failed: {ex.Message}");
                PostWebMessage(new
                {
                    type = "conversations_loaded",
                    conversations = new JArray(),
                });
            }
        }

        private async Task HandleLoadConversation(string conversationId)
        {
            try
            {
                var response = await AuthedRequest(
                    HttpMethod.Get,
                    $"/v1/chat/conversations/{conversationId}");
                if (response == null || !response.IsSuccessStatusCode) return;

                var data = JObject.Parse(
                    await response.Content.ReadAsStringAsync());

                var messagesList = new JArray();
                if (data["messages"] != null)
                {
                    int idx = 0;
                    foreach (var m in data["messages"])
                    {
                        messagesList.Add(new JObject
                        {
                            ["id"] = m["message_id"]?.ToString()
                                     ?? $"hist-{idx++}",
                            ["role"] = m["role"]?.ToString(),
                            ["content"] = m["content"]?.ToString() ?? "",
                            ["tool_calls"] = m["tool_calls"],
                        });
                    }
                }

                PostWebMessage(new
                {
                    type = "conversation_loaded",
                    conversationId,
                    messages = messagesList,
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[Agent] Load conversation failed: {ex.Message}");
            }
        }

        private async Task HandleDeleteConversation(string conversationId)
        {
            try
            {
                await AuthedRequest(
                    HttpMethod.Delete,
                    $"/v1/chat/conversations/{conversationId}");
                await HandleLoadConversations();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[Agent] Delete conversation failed: {ex.Message}");
            }
        }

        // ── HITL Approval ───────────────────────────────────────────────

        private async Task HandleApprovalResponse(
            string approvalId, bool approved, string reason)
        {
            if (string.IsNullOrEmpty(approvalId)) return;
            try
            {
                var payload = JsonConvert.SerializeObject(new
                {
                    approved,
                    reason = reason ?? "",
                });
                var content = new StringContent(
                    payload, Encoding.UTF8, "application/json");

                await AuthedRequest(
                    HttpMethod.Post,
                    $"/v1/chat/approvals/{approvalId}",
                    content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[Agent] Approval response failed: {ex.Message}");
            }
        }

        // ── Chat — SSE streaming (production parity with VS Code ext) ──

        private async Task HandleUserInput(string text, string conversationId, JToken attachments = null)
        {
            if (string.IsNullOrEmpty(authToken))
            {
                PostWebMessage(new { type = "require_login" });
                return;
            }
            if (_workspaceMode == "local")
                await HandleUserInputLocal(text, conversationId, attachments);
            else
                await HandleUserInputCloud(text, conversationId);
        }

        // ── LOCAL SERVER MODE (WebSocket + local tool execution) ─────────
        private async Task HandleUserInputLocal(string text, string conversationId, JToken attachments = null)
        {
            PostWebMessage(new { type = "thinking", context = new { } });
            try
            {
                if (_localSocket == null || !_localSocket.IsConnected)
                {
                    _localSocket?.Dispose();
                    var ws = GetWorkspaceRoot();
                    _localSocket = new LocalAgentSocket(
                        ApiUrl, authToken, TenantDomain,
                        new LocalToolExecutor(ws),
                        frame => DispatchWsEvent(frame, conversationId));
                    await _localSocket.ConnectAsync();
                }
                await _localSocket.SendChatAsync(text, conversationId, GatherContext(), attachments);
            }
            catch (Exception ex)
            {
                PostWebMessage(new { type = "agent_response",
                    text = $"\n\n**Error (Local mode):** Could not connect to the agent over WebSocket. {ex.Message}",
                    isNew = true });
                PostWebMessage(new { type = "agent_response_end", conversationId });
            }
        }

        /// <summary>Map a WS {type:event,event:...} frame to the same webview
        /// messages the cloud SSE path posts.</summary>
        private void DispatchWsEvent(JObject data, string conversationId)
        {
            var ev = data["event"]?.ToString() ?? "";
            switch (ev)
            {
                case "message.start":
                    PostWebMessage(new { type = "agent_response", text = "", isNew = true });
                    break;
                case "message.delta":
                    PostWebMessage(new { type = "agent_response", text = data["content"]?.ToString() ?? "", isNew = false });
                    break;
                case "thinking.delta":
                    PostWebMessage(new { type = "thinking_delta", text = data["content"]?.ToString() ?? "" });
                    break;
                case "tool.start":
                    PostWebMessage(new { type = "agent_response", text = "", isNew = false,
                        tool_calls = new[] { new { name = data["toolName"]?.ToString(), args = data["toolArgs"] ?? new JObject(), status = "running" } } });
                    break;
                case "tool.end":
                    PostWebMessage(new { type = "agent_response", text = "", isNew = false,
                        tool_calls = new[] { new { name = data["toolName"]?.ToString(), args = (object)null, status = "completed" } } });
                    break;
                case "plan":
                    PostWebMessage(new { type = "plan_update", steps = data["steps"] ?? new JArray() });
                    break;
                case "artifact":
                    // Part 2 — model-generated media. Fetch bytes (authed) and hand
                    // the webview a renderable data URI.
                    _ = FetchArtifactAsync(
                        data["fileId"]?.ToObject<long?>(),
                        data["kind"]?.ToString(),
                        data["mediaType"]?.ToString());
                    break;
                case "warning":
                    PostWebMessage(new { type = "warning", message = data["message"]?.ToString() ?? "" });
                    break;
                case "citations":
                    PostWebMessage(new { type = "citations_update", sources = data["sources"] ?? new JArray() });
                    break;
                case "approval.request":
                    PostWebMessage(new { type = "approval_request",
                        approvalId = data["approvalId"]?.ToString(), toolName = data["toolName"]?.ToString(),
                        toolArgs = data["toolArgs"] ?? new JObject(), timeout_s = data["timeout_s"]?.Value<int>() ?? 300 });
                    break;
                case "message.end":
                    PostWebMessage(new { type = "agent_response_end",
                        conversationId = data["conversationId"]?.ToString() ?? data["conversation_id"]?.ToString() ?? conversationId,
                        conversationTokens = data["conversationTokens"]?.Value<int>() ?? 0,
                        conversationCostUsd = data["conversationCostUsd"]?.Value<double>() ?? 0.0 });
                    break;
                case "done":
                    PostWebMessage(new { type = "agent_response_end", conversationId });
                    break;
                case "error":
                    PostWebMessage(new { type = "agent_response", text = "\n\n**Error:** " + (data["message"]?.ToString() ?? "Unknown error"), isNew = true });
                    break;
            }
        }

        /// <summary>Fetch a generated media file's bytes (authenticated) and forward a
        /// renderable data URI to the webview (the webview can't call the API itself).</summary>
        private async Task FetchArtifactAsync(long? fileId, string kind, string mediaType)
        {
            if (fileId == null) return;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl.TrimEnd('/')}/v1/media/{fileId}");
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authToken}");
                req.Headers.TryAddWithoutValidation("X-Tenant-Domain", TenantDomain);
                var res = await httpClient.SendAsync(req);
                if (!res.IsSuccessStatusCode) return;
                var bytes = await res.Content.ReadAsByteArrayAsync();
                var mt = !string.IsNullOrEmpty(mediaType)
                    ? mediaType
                    : (res.Content.Headers.ContentType?.MediaType ?? "image/png");
                var dataUri = $"data:{mt};base64,{Convert.ToBase64String(bytes)}";
                PostWebMessage(new { type = "artifact", kind = kind ?? "image", mediaType = mt, dataUri });
            }
            catch { /* best-effort — a failed media fetch never breaks the turn */ }
        }

        // ── Workspace context (open solution dir + active document) ──────
        private string GetWorkspaceRoot()
        {
            try
            {
                var dte = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var sln = dte?.Solution?.FullName;
                if (!string.IsNullOrEmpty(sln)) return System.IO.Path.GetDirectoryName(sln);
                var doc = dte?.ActiveDocument?.FullName;
                if (!string.IsNullOrEmpty(doc)) return System.IO.Path.GetDirectoryName(doc);
            }
            catch { }
            return "";
        }

        private JObject GatherContext()
        {
            var ctx = new JObject();
            var root = GetWorkspaceRoot();
            if (!string.IsNullOrEmpty(root)) ctx["workspaceRoot"] = root;
            try
            {
                var dte = Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider
                    .GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var doc = dte?.ActiveDocument;
                if (doc != null && !string.IsNullOrEmpty(doc.FullName))
                {
                    ctx["activeFile"] = doc.FullName;
                    try
                    {
                        var full = System.IO.File.ReadAllText(doc.FullName);
                        const int cap = 24000;
                        ctx["activeFileContent"] = full.Length > cap ? full.Substring(0, cap) : full;
                        ctx["activeFileTruncated"] = full.Length > cap;
                    }
                    catch { }
                    try
                    {
                        if (doc.Selection is EnvDTE.TextSelection ts && !string.IsNullOrEmpty(ts.Text))
                            ctx["selectedText"] = ts.Text.Length > 8000 ? ts.Text.Substring(0, 8000) : ts.Text;
                    }
                    catch { }
                }
            }
            catch { }
            return ctx;
        }

        // ── CLOUD SERVER MODE (HTTP SSE) ─────────────────────────────────
        private async Task HandleUserInputCloud(
            string text, string conversationId)
        {
            // Cancel any previous stream
            _streamCts?.Cancel();
            _streamCts = new CancellationTokenSource();
            var ct = _streamCts.Token;

            try
            {
                // Signal the webview that the agent is thinking
                PostWebMessage(new { type = "thinking", context = new { } });

                var payload = new JObject
                {
                    ["message"] = text,
                    ["stream"] = true,
                    ["provider"] = "visualstudio",
                };
                if (!string.IsNullOrEmpty(conversationId))
                    payload["conversation_id"] = conversationId;

                var content = new StringContent(
                    payload.ToString(), Encoding.UTF8, "application/json");

                var response = await AuthedRequest(
                    HttpMethod.Post, "/v1/chat", content);
                if (response == null) return;

                if (!response.IsSuccessStatusCode)
                {
                    string detail = $"Server error ({(int)response.StatusCode})";
                    try
                    {
                        var body = JObject.Parse(
                            await response.Content.ReadAsStringAsync());
                        detail = body["detail"]?.ToString()
                                 ?? body["message"]?.ToString()
                                 ?? detail;
                    }
                    catch { /* non-JSON */ }

                    PostWebMessage(new
                    {
                        type = "agent_response",
                        text = $"\n\n**Error:** {detail}",
                        isNew = true,
                    });
                    PostWebMessage(new
                    {
                        type = "agent_response_end",
                        conversationId,
                    });
                    return;
                }

                // ── Parse the SSE stream ────────────────────────────────
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(stream))
                {
                    string returnedConversationId = null;
                    bool isNew = true;

                    while (!reader.EndOfStream && !ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (!line.StartsWith("data: ")) continue;

                        var dataStr = line.Substring(6).Trim();
                        if (dataStr == "[DONE]") break;

                        JObject data;
                        try
                        {
                            data = JObject.Parse(dataStr);
                        }
                        catch
                        {
                            Debug.WriteLine(
                                $"[Agent] Unparseable SSE: {dataStr}");
                            continue;
                        }

                        var eventType = data["event"]?.ToString() ?? "";

                        // Capture conversation_id from any frame
                        if (data["conversationId"] != null)
                            returnedConversationId =
                                data["conversationId"].ToString();
                        else if (data["conversation_id"] != null)
                            returnedConversationId =
                                data["conversation_id"].ToString();

                        switch (eventType)
                        {
                            // ── Text streaming ──────────────────────
                            case "message.start":
                                isNew = true;
                                PostWebMessage(new
                                {
                                    type = "agent_response",
                                    text = "",
                                    isNew = true,
                                });
                                break;

                            case "message.delta":
                                PostWebMessage(new
                                {
                                    type = "agent_response",
                                    text = data["content"]?.ToString() ?? "",
                                    isNew,
                                });
                                isNew = false;
                                break;

                            // ── Tool timeline ───────────────────────
                            case "tool.start":
                                PostWebMessage(new
                                {
                                    type = "agent_response",
                                    text = "",
                                    isNew,
                                    tool_calls = new[]
                                    {
                                        new
                                        {
                                            name = data["toolName"]?.ToString(),
                                            args = data["toolArgs"] ?? new JObject(),
                                            status = "running",
                                        }
                                    },
                                });
                                isNew = false;
                                break;

                            case "tool.end":
                                PostWebMessage(new
                                {
                                    type = "agent_response",
                                    text = "",
                                    isNew = false,
                                    tool_calls = new[]
                                    {
                                        new
                                        {
                                            name = data["toolName"]?.ToString(),
                                            args = (object)null,
                                            status = "completed",
                                        }
                                    },
                                });
                                break;

                            // ── Plan view ───────────────────────────
                            case "plan":
                                PostWebMessage(new
                                {
                                    type = "plan_update",
                                    steps = data["steps"] ?? new JArray(),
                                });
                                break;

                            // ── Brain citations ─────────────────────
                            case "citations":
                                PostWebMessage(new
                                {
                                    type = "citations_update",
                                    sources = data["sources"] ?? new JArray(),
                                });
                                break;

                            // ── HITL approval request ────────────────
                            case "approval.request":
                                PostWebMessage(new
                                {
                                    type = "approval_request",
                                    approvalId =
                                        data["approvalId"]?.ToString(),
                                    toolName =
                                        data["toolName"]?.ToString(),
                                    toolArgs =
                                        data["toolArgs"] ?? new JObject(),
                                    timeout_s =
                                        data["timeout_s"]?.Value<int>() ?? 300,
                                });
                                break;

                            // ── Turn complete (cost/token meter) ─────
                            case "message.end":
                                PostWebMessage(new
                                {
                                    type = "agent_response_end",
                                    conversationId =
                                        returnedConversationId
                                        ?? conversationId,
                                    conversationTokens =
                                        data["conversationTokens"]
                                            ?.Value<int>() ?? 0,
                                    conversationCostUsd =
                                        data["conversationCostUsd"]
                                            ?.Value<double>() ?? 0.0,
                                });
                                break;

                            // ── Errors ──────────────────────────────
                            case "error":
                                PostWebMessage(new
                                {
                                    type = "agent_response",
                                    text = $"\n\n**Error:** "
                                           + (data["message"]?.ToString()
                                              ?? "Unknown error"),
                                    isNew = true,
                                });
                                break;

                            // ── Pings / unknown — silently skip ──────
                            default:
                                break;
                        }
                    }

                    // If the stream ended without a message.end, close cleanly
                    PostWebMessage(new
                    {
                        type = "agent_response_end",
                        conversationId =
                            returnedConversationId ?? conversationId,
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // User cancelled (new_chat / stop_generation) — normal.
                PostWebMessage(new
                {
                    type = "agent_response_end",
                    conversationId,
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Agent] Chat error: {ex.Message}");
                PostWebMessage(new
                {
                    type = "agent_response",
                    text = $"\n\n**Error:** {ex.Message}",
                    isNew = true,
                });
                PostWebMessage(new
                {
                    type = "agent_response_end",
                    conversationId,
                });
            }
        }
    }
}
