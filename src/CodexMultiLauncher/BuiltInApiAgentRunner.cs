using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexMultiLauncher;

public sealed record ApiConnectionTestResult(bool Ok, long LatencyMs, string Summary);

/// <summary>
/// Zero-download built-in C# autonomous coding agent engine supporting OpenAI-compatible,
/// Anthropic Messages, and Google Gemini native protocols with real-time SSE token &amp; thinking
/// streaming (`reasoning_content`, `thinking_delta`, Gemini `thought` parts) and local workspace tools.
/// </summary>
public sealed class BuiltInApiAgentRunner : IGroupChatRunner
{
    private const int MaxToolIterations = 32;
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    private readonly AgentProviderStore _store;
    private readonly Action<string, long, long>? _onTokenUsage;
    private readonly HttpClient _http;

    public BuiltInApiAgentRunner(AgentProviderStore store, Action<string, long, long>? onTokenUsage = null, HttpClient? httpClient = null)
    {
        _store = store;
        _onTokenUsage = onTokenUsage;
        _http = httpClient ?? SharedHttp;
    }

    public async Task<ApiConnectionTestResult> TestConnectionAsync(UniversalAgentRecord record, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var dummyWs = new CodexWorkspace(
            AgentProviderStore.ToWorkspaceId(record),
            record.DisplayName,
            CodexWorkspaceKind.Managed,
            _store.GetIsolatedHomeDirectory(record.Id),
            null,
            Client: ClientKind.DirectApi,
            ProviderId: record.Id,
            DefaultModel: record.Model);

        var run = new GroupChatRun(
            new GroupChatAgent("probe", "Probe", dummyWs.Id, record.Model),
            dummyWs,
            _store.GetIsolatedHomeDirectory(record.Id),
            "请仅回复 OK 两个字母用于连通性自检，不要调用任何工具。",
            ChatSandboxMode.ReadOnly);

        var reply = await ExecuteAgentLoopAsync(record, run, new Progress<GroupChatUpdate>(), allowTools: false, token).ConfigureAwait(false);
        sw.Stop();
        var preview = reply.Length > 60 ? reply[..60] + "…" : reply;
        return new ApiConnectionTestResult(true, sw.ElapsedMilliseconds, $"连通正常 ({sw.ElapsedMilliseconds} ms) · 模型响应：{preview}");
    }

    public async Task<string> RunAsync(GroupChatRun request, IProgress<GroupChatUpdate> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Workspace.Client != ClientKind.DirectApi)
            throw new InvalidOperationException("此身份必须绑定免下载直连 API 智能体。");

        var record = _store.FindById(request.Workspace.ProviderId ?? request.Workspace.Id)
            ?? throw new InvalidOperationException("找不到此直连 API 智能体的配置，请重新检查账号池。");

        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new InvalidOperationException("项目工作目录不存在。");

        return await ExecuteAgentLoopAsync(record, request, progress, allowTools: true, token).ConfigureAwait(false);
    }

    private async Task<string> ExecuteAgentLoopAsync(
        UniversalAgentRecord record,
        GroupChatRun request,
        IProgress<GroupChatUpdate> progress,
        bool allowTools,
        CancellationToken token)
    {
        var model = !string.IsNullOrWhiteSpace(request.Agent.Model) ? request.Agent.Model! : record.Model;
        var apiKey = DpapiSecretProtector.Unprotect(record.EncryptedApiKey);
        var writable = allowTools && request.SandboxMode == ChatSandboxMode.WorkspaceWrite;
        var steps = new List<ChatExecutionStep>();

        var systemPrompt = BuildSystemInstruction(request.WorkingDirectory, writable);

        return record.Protocol switch
        {
            ApiProtocolKind.AnthropicMessages => await RunAnthropicLoopAsync(record.BaseUrl, model, apiKey, systemPrompt, request, allowTools, writable, steps, progress, token).ConfigureAwait(false),
            ApiProtocolKind.GeminiNative => await RunGeminiLoopAsync(record.BaseUrl, model, apiKey, systemPrompt, request, allowTools, writable, steps, progress, token).ConfigureAwait(false),
            _ => await RunOpenAiCompatibleLoopAsync(record.BaseUrl, model, apiKey, systemPrompt, request, allowTools, writable, steps, progress, token).ConfigureAwait(false),
        };
    }

    private static string BuildSystemInstruction(string workingDirectory, bool writable) =>
        $"""
        你是运行在「Hyperion · 项目工作台」中的全功能研发智能体。
        当前绑定的本地项目目录为：{workingDirectory}
        当前权限模式：{(writable ? "Agent 可读写实操模式（可使用 list_files / search_files / read_file / write_file / edit_file / run_command 直接阅读和修改项目代码并运行验证命令）" : "Ask 只读分析模式（可使用 list_files / search_files / read_file 阅读与搜索真实代码，严禁修改文件）")}。
        请保持原汁原味、清晰简洁的表达与 GitHub 风格 Markdown。在需要查看或修改项目文件时主动调用工具，不要凭空猜测文件内容。
        """;

    private static JsonArray BuildOpenAiInitialMessages(string systemPrompt, GroupChatRun request)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
        };

        var turns = request.NativeTurns;
        if (turns is { Count: > 0 })
        {
            for (var i = 0; i < turns.Count - 1; i++)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = turns[i].Role == "assistant" ? "assistant" : "user",
                    ["content"] = turns[i].Text,
                });
            }
        }

        var lastUserText = turns is { Count: > 0 } ? turns[^1].Text : request.Prompt;
        if (request.Attachments is { Count: > 0 })
        {
            var contentArr = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = lastUserText },
            };
            foreach (var att in request.Attachments)
            {
                if (string.IsNullOrWhiteSpace(att.Base64Data)) continue;
                var mime = string.IsNullOrWhiteSpace(att.MimeType) ? "image/png" : att.MimeType;
                contentArr.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{att.Base64Data}" },
                });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = contentArr });
        }
        else
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = lastUserText });
        }

        return messages;
    }

    private static JsonArray BuildAnthropicInitialMessages(GroupChatRun request)
    {
        var messages = new JsonArray();
        var turns = request.NativeTurns;

        void AppendTurn(string role, JsonNode content)
        {
            if (messages.Count > 0 && messages[^1] is JsonObject prev && prev["role"]?.GetValue<string>() == role)
            {
                // Coalesce consecutive same-role turns as Anthropic Messages API requires alternating roles
                var prevBlocks = prev["content"] as JsonArray;
                if (prevBlocks is null)
                {
                    prevBlocks = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prev["content"]?.GetValue<string>() ?? string.Empty } };
                    prev["content"] = prevBlocks;
                }
                if (content is JsonArray nextArr)
                {
                    foreach (var item in nextArr) prevBlocks.Add(item?.DeepClone());
                }
                else
                {
                    prevBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = content.GetValue<string>() });
                }
                return;
            }
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }

        if (turns is { Count: > 1 })
        {
            for (var i = 0; i < turns.Count - 1; i++)
            {
                var role = turns[i].Role == "assistant" ? "assistant" : "user";
                if (messages.Count == 0 && role == "assistant") continue;
                AppendTurn(role, JsonValue.Create(turns[i].Text)!);
            }
        }

        var lastUserText = turns is { Count: > 0 } ? turns[^1].Text : request.Prompt;
        if (request.Attachments is { Count: > 0 })
        {
            var blocks = new JsonArray();
            foreach (var att in request.Attachments)
            {
                if (string.IsNullOrWhiteSpace(att.Base64Data)) continue;
                blocks.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = string.IsNullOrWhiteSpace(att.MimeType) ? "image/png" : att.MimeType,
                        ["data"] = att.Base64Data,
                    },
                });
            }
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = lastUserText });
            AppendTurn("user", blocks);
        }
        else
        {
            AppendTurn("user", JsonValue.Create(lastUserText)!);
        }

        return messages;
    }

    private static JsonArray BuildGeminiInitialContents(GroupChatRun request)
    {
        var contents = new JsonArray();
        var turns = request.NativeTurns;

        void AppendGeminiTurn(string role, JsonArray parts)
        {
            if (contents.Count > 0 && contents[^1] is JsonObject prev && prev["role"]?.GetValue<string>() == role && prev["parts"] is JsonArray prevParts)
            {
                foreach (var p in parts) prevParts.Add(p?.DeepClone());
                return;
            }
            contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
        }

        if (turns is { Count: > 1 })
        {
            for (var i = 0; i < turns.Count - 1; i++)
            {
                var role = turns[i].Role == "assistant" ? "model" : "user";
                if (contents.Count == 0 && role == "model") continue;
                AppendGeminiTurn(role, new JsonArray { new JsonObject { ["text"] = turns[i].Text } });
            }
        }

        var lastUserText = turns is { Count: > 0 } ? turns[^1].Text : request.Prompt;
        var lastParts = new JsonArray();
        if (request.Attachments is { Count: > 0 })
        {
            foreach (var att in request.Attachments)
            {
                if (string.IsNullOrWhiteSpace(att.Base64Data)) continue;
                lastParts.Add(new JsonObject
                {
                    ["inlineData"] = new JsonObject
                    {
                        ["mimeType"] = string.IsNullOrWhiteSpace(att.MimeType) ? "image/png" : att.MimeType,
                        ["data"] = att.Base64Data,
                    },
                });
            }
        }
        lastParts.Add(new JsonObject { ["text"] = lastUserText });
        AppendGeminiTurn("user", lastParts);
        return contents;
    }

    private sealed class StreamedToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }

    private async Task<string> RunOpenAiCompatibleLoopAsync(
        string baseUrl,
        string model,
        string? apiKey,
        string systemPrompt,
        GroupChatRun request,
        bool allowTools,
        bool writable,
        List<ChatExecutionStep> steps,
        IProgress<GroupChatUpdate> progress,
        CancellationToken token)
    {
        var endpoint = NormalizeOpenAiEndpoint(baseUrl);
        var messages = BuildOpenAiInitialMessages(systemPrompt, request);

        var tools = allowTools ? BuildOpenAiToolsJson(writable) : null;
        var stepSeq = 0;
        var cumulativeThinking = new StringBuilder();

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            token.ThrowIfCancellationRequested();
            var reqStepId = $"api-req-{++stepSeq}";
            steps.Add(new ChatExecutionStep(reqStepId, "thinking", $"请求 {model} 推理与规划（第 {iteration + 1} 轮）", "running"));
            progress.Report(new GroupChatUpdate(string.Empty, $"正在连接 {model}…", steps.ToArray(), cumulativeThinking.Length > 0 ? cumulativeThinking.ToString() : null));

            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages.DeepClone(),
                ["stream"] = allowTools, // Stream real chats; keep ping probes lightweight
            };
            if (allowTools)
            {
                payload["stream_options"] = new JsonObject { ["include_usage"] = true };
            }
            var isReasoningModel = !string.IsNullOrWhiteSpace(request.Agent.ReasoningEffort)
                || model.Contains("reasoner", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase);
            if (!isReasoningModel)
            {
                payload["temperature"] = 0.2;
            }
            if (!string.IsNullOrWhiteSpace(request.Agent.ReasoningEffort))
            {
                payload["reasoning_effort"] = MapOpenAiReasoningEffort(request.Agent.ReasoningEffort);
            }
            if (!string.IsNullOrWhiteSpace(request.Agent.ServiceTier) &&
                !string.Equals(request.Agent.ServiceTier, "default", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.Agent.ServiceTier, "standard", StringComparison.OrdinalIgnoreCase))
            {
                payload["service_tier"] = request.Agent.ServiceTier.Trim().ToLowerInvariant();
            }
            if (tools is not null && tools.Count > 0)
            {
                payload["tools"] = tools.DeepClone();
            }

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(apiKey))
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                // Some 3rd-party OpenAI-compatible gateways reject stream_options / reasoning_effort / service_tier; strip and retry once cleanly
                var strippedAny = false;
                if ((int)resp.StatusCode == 400)
                {
                    if (errBody.Contains("stream_options", StringComparison.OrdinalIgnoreCase)) { payload.Remove("stream_options"); strippedAny = true; }
                    if (errBody.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)) { payload.Remove("reasoning_effort"); strippedAny = true; }
                    if (errBody.Contains("service_tier", StringComparison.OrdinalIgnoreCase)) { payload.Remove("service_tier"); strippedAny = true; }
                }
                if (strippedAny)
                {
                    using var retryReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrWhiteSpace(apiKey))
                        retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using var retryResp = await _http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (!retryResp.IsSuccessStatusCode)
                    {
                        var retryErr = await retryResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        throw new InvalidOperationException($"API 请求失败（HTTP {(int)retryResp.StatusCode}）：{TrimError(retryErr)}");
                    }
                    return await ConsumeOpenAiResponseAndContinueAsync(retryResp, iteration, messages, allowTools, writable, request, steps, cumulativeThinking, progress, token).ConfigureAwait(false)
                        ?? await RunRemainingOpenAiIterationsAsync(endpoint, model, apiKey, messages, tools, isReasoningModel, iteration + 1, allowTools, writable, request, steps, stepSeq, cumulativeThinking, progress, token).ConfigureAwait(false);
                }
                throw new InvalidOperationException($"API 请求失败（HTTP {(int)resp.StatusCode}）：{TrimError(errBody)}");
            }

            var finalContent = await ConsumeOpenAiResponseAndContinueAsync(resp, iteration, messages, allowTools, writable, request, steps, cumulativeThinking, progress, token).ConfigureAwait(false);
            if (finalContent is not null)
                return finalContent;
        }

        throw new InvalidOperationException($"内置直连 Agent 连续调用工具超过 {MaxToolIterations} 轮上限，已安全中止。");
    }

    private async Task<string> RunRemainingOpenAiIterationsAsync(
        string endpoint,
        string model,
        string? apiKey,
        JsonArray messages,
        JsonArray? tools,
        bool isReasoningModel,
        int startIteration,
        bool allowTools,
        bool writable,
        GroupChatRun request,
        List<ChatExecutionStep> steps,
        int stepSeq,
        StringBuilder cumulativeThinking,
        IProgress<GroupChatUpdate> progress,
        CancellationToken token)
    {
        for (var iteration = startIteration; iteration < MaxToolIterations; iteration++)
        {
            token.ThrowIfCancellationRequested();
            steps.Add(new ChatExecutionStep($"api-req-{++stepSeq}", "thinking", $"请求 {model} 推理与规划（第 {iteration + 1} 轮）", "running"));
            progress.Report(new GroupChatUpdate(string.Empty, $"正在连接 {model}…", steps.ToArray(), cumulativeThinking.Length > 0 ? cumulativeThinking.ToString() : null));

            var payload = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages.DeepClone(),
                ["stream"] = allowTools,
            };
            if (!isReasoningModel) payload["temperature"] = 0.2;
            if (tools is not null && tools.Count > 0) payload["tools"] = tools.DeepClone();

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                throw new InvalidOperationException($"API 请求失败（HTTP {(int)resp.StatusCode}）：{TrimError(errBody)}");
            }

            var finalContent = await ConsumeOpenAiResponseAndContinueAsync(resp, iteration, messages, allowTools, writable, request, steps, cumulativeThinking, progress, token).ConfigureAwait(false);
            if (finalContent is not null)
                return finalContent;
        }

        throw new InvalidOperationException($"内置直连 Agent 连续调用工具超过 {MaxToolIterations} 轮上限，已安全中止。");
    }

    private async Task<string?> ConsumeOpenAiResponseAndContinueAsync(
        HttpResponseMessage resp,
        int iteration,
        JsonArray messages,
        bool allowTools,
        bool writable,
        GroupChatRun request,
        List<ChatExecutionStep> steps,
        StringBuilder cumulativeThinking,
        IProgress<GroupChatUpdate> progress,
        CancellationToken token)
    {
        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var isSse = string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

        var contentBuilder = new StringBuilder();
        var turnThinkingBuilder = new StringBuilder();
        var toolMap = new SortedDictionary<int, StreamedToolCallBuilder>();

        if (isSse)
        {
            if (cumulativeThinking.Length > 0) cumulativeThinking.AppendLine().AppendLine();
            var baseThinkingPrefix = cumulativeThinking.ToString();

            using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        var inTok = u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var pv) ? pv : 0L;
                        var outTok = u.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var cv) ? cv : 0L;
                        if (inTok > 0 || outTok > 0) _onTokenUsage?.Invoke(request.Workspace.Id, inTok, outTok);
                    }

                    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                        continue;

                    var choice = choices[0];
                    if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                        continue;

                    var changed = false;
                    foreach (var rKey in new[] { "reasoning_content", "reasoning" })
                    {
                        if (delta.TryGetProperty(rKey, out var rc) && rc.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rc.GetString()))
                        {
                            turnThinkingBuilder.Append(rc.GetString());
                            changed = true;
                        }
                    }

                    if (delta.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(dc.GetString()))
                    {
                        contentBuilder.Append(dc.GetString());
                        changed = true;
                    }

                    if (delta.TryGetProperty("tool_calls", out var dtc) && dtc.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in dtc.EnumerateArray())
                        {
                            var idx = tc.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
                            if (!toolMap.TryGetValue(idx, out var builder))
                            {
                                builder = new StreamedToolCallBuilder();
                                toolMap[idx] = builder;
                            }
                            if (tc.TryGetProperty("id", out var idP) && idP.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(idP.GetString()))
                                builder.Id = idP.GetString()!;
                            if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                            {
                                if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(nm.GetString()))
                                    builder.Name = nm.GetString()!;
                                if (fn.TryGetProperty("arguments", out var ag) && ag.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ag.GetString()))
                                    builder.Arguments.Append(ag.GetString());
                            }
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        var liveThink = turnThinkingBuilder.Length > 0
                            ? baseThinkingPrefix + turnThinkingBuilder
                            : (baseThinkingPrefix.Length > 0 ? baseThinkingPrefix.TrimEnd() : null);
                        var activity = toolMap.Count > 0
                            ? $"正在生成工具调用参数 ({toolMap.Values.Last().Name})…"
                            : (contentBuilder.Length > 0 ? "正在流式输出…" : "正在深度思考…");
                        progress.Report(new GroupChatUpdate(contentBuilder.ToString(), activity, steps.ToArray(), liveThink));
                    }
                }
                catch (JsonException)
                {
                }
            }

            if (turnThinkingBuilder.Length > 0)
                cumulativeThinking.Append(turnThinkingBuilder);
            else if (cumulativeThinking.Length >= 4)
                cumulativeThinking.Length -= 4; // remove trailing double newline if no new thinking
        }
        else
        {
            var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var inTok = usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var pv) ? pv : 0L;
                var outTok = usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var cv) ? cv : 0L;
                if (inTok > 0 || outTok > 0) _onTokenUsage?.Invoke(request.Workspace.Id, inTok, outTok);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new InvalidOperationException("API 响应未包含有效 choices。");

            var msg = choices[0].GetProperty("message");
            if (msg.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                contentBuilder.Append(cProp.GetString());

            foreach (var rKey in new[] { "reasoning_content", "reasoning" })
            {
                if (msg.TryGetProperty(rKey, out var rcProp) && rcProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rcProp.GetString()))
                {
                    turnThinkingBuilder.Append(rcProp.GetString());
                    if (cumulativeThinking.Length > 0) cumulativeThinking.AppendLine().AppendLine();
                    cumulativeThinking.Append(turnThinkingBuilder);
                }
            }

            if (allowTools && msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                var idx = 0;
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var b = new StreamedToolCallBuilder
                    {
                        Id = tc.TryGetProperty("id", out var idP) ? idP.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                    };
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        b.Name = fn.TryGetProperty("name", out var np) ? np.GetString() ?? "tool" : "tool";
                        if (fn.TryGetProperty("arguments", out var argP) && argP.ValueKind == JsonValueKind.String)
                            b.Arguments.Append(argP.GetString());
                    }
                    toolMap[idx++] = b;
                }
            }
        }

        steps[^1] = steps[^1] with { Status = "completed" };
        var currentText = contentBuilder.ToString();
        var currentThinking = cumulativeThinking.Length > 0 ? cumulativeThinking.ToString() : null;

        if (allowTools && toolMap.Count > 0)
        {
            var tcArray = new JsonArray();
            foreach (var item in toolMap.Values)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) item.Id = "call_" + Guid.NewGuid().ToString("N")[..10];
                tcArray.Add(new JsonObject
                {
                    ["id"] = item.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = item.Name,
                        ["arguments"] = item.Arguments.Length > 0 ? item.Arguments.ToString() : "{}",
                    },
                });
            }

            var assistantMsg = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = currentText,
                ["tool_calls"] = tcArray,
            };
            if (turnThinkingBuilder.Length > 0)
            {
                assistantMsg["reasoning_content"] = turnThinkingBuilder.ToString();
            }
            messages.Add(assistantMsg);

            foreach (var tc in toolMap.Values)
            {
                var fnName = string.IsNullOrWhiteSpace(tc.Name) ? "tool" : tc.Name;
                var fnArgsRaw = tc.Arguments.Length > 0 ? tc.Arguments.ToString() : "{}";
                var stepId = $"tool-{steps.Count + 1}";
                var step = BuildLocalToolStep(stepId, fnName, fnArgsRaw, "running");
                steps.Add(step);
                progress.Report(new GroupChatUpdate(currentText, step.Title, steps.ToArray(), currentThinking));

                var toolResult = await ExecuteLocalToolSafeAsync(fnName, fnArgsRaw, request.WorkingDirectory, writable, token).ConfigureAwait(false);
                var failed = toolResult.StartsWith("ExitCode:", StringComparison.Ordinal) && !toolResult.StartsWith("ExitCode: 0", StringComparison.Ordinal);
                steps[^1] = step with { Status = failed ? "failed" : "completed" };
                progress.Report(new GroupChatUpdate(currentText, $"已完成 {fnName}", steps.ToArray(), currentThinking));

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tc.Id,
                    ["content"] = toolResult,
                });
            }

            return null; // continue tool loop
        }

        if (string.IsNullOrWhiteSpace(currentText))
            currentText = "（模型已完成工具操作，未附加额外说明文本。）";

        progress.Report(new GroupChatUpdate(currentText, "已完成", steps.ToArray(), currentThinking));
        return currentText;
    }

    private sealed class AnthropicStreamBlock
    {
        public string Type { get; set; } = "text";
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public StringBuilder Text { get; } = new();
        public StringBuilder JsonInput { get; } = new();
    }

    private async Task<string> RunAnthropicLoopAsync(
        string baseUrl,
        string model,
        string? apiKey,
        string systemPrompt,
        GroupChatRun request,
        bool allowTools,
        bool writable,
        List<ChatExecutionStep> steps,
        IProgress<GroupChatUpdate> progress,
        CancellationToken token)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var endpoint = trimmed.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? trimmed + "/messages"
                : trimmed + "/v1/messages";

        var messages = BuildAnthropicInitialMessages(request);
        var tools = allowTools ? BuildAnthropicToolsJson(writable) : null;
        var stepSeq = 0;
        var cumulativeThinking = new StringBuilder();

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            token.ThrowIfCancellationRequested();
            steps.Add(new ChatExecutionStep($"anth-req-{++stepSeq}", "thinking", $"请求 Claude ({model}) 第 {iteration + 1} 轮", "running"));
            progress.Report(new GroupChatUpdate(string.Empty, $"正在连接 {model}…", steps.ToArray(), cumulativeThinking.Length > 0 ? cumulativeThinking.ToString() : null));

            var anthropicBudget = MapAnthropicThinkingBudget(request.Agent.ReasoningEffort);
            var maxTokens = anthropicBudget is > 0 ? Math.Max(16384, anthropicBudget.Value + 8192) : 8192;

            var payload = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = maxTokens,
                ["system"] = systemPrompt,
                ["messages"] = messages.DeepClone(),
                ["stream"] = allowTools,
            };
            if (allowTools && anthropicBudget is > 0)
            {
                payload["thinking"] = new JsonObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = anthropicBudget.Value,
                };
            }
            if (string.Equals(request.Agent.ServiceTier, "fast", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.Agent.ServiceTier, "priority", StringComparison.OrdinalIgnoreCase))
            {
                payload["speed"] = "fast";
            }
            if (tools is not null && tools.Count > 0) payload["tools"] = tools.DeepClone();

            HttpResponseMessage? activeResp = null;
            try
            {
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrWhiteSpace(apiKey))
                    httpReq.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                httpReq.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

                activeResp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!activeResp.IsSuccessStatusCode)
                {
                    var errBody = await activeResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    var canRetry = false;
                    if ((int)activeResp.StatusCode == 400)
                    {
                        if (errBody.Contains("speed", StringComparison.OrdinalIgnoreCase)) { payload.Remove("speed"); canRetry = true; }
                        if (errBody.Contains("thinking", StringComparison.OrdinalIgnoreCase)) { payload.Remove("thinking"); payload["max_tokens"] = 8192; canRetry = true; }
                    }
                    if (canRetry)
                    {
                        activeResp.Dispose();
                        using var retryReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
                        {
                            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                        };
                        retryReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                        retryReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        if (!string.IsNullOrWhiteSpace(apiKey))
                            retryReq.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                        retryReq.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                        activeResp = await _http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    }
                    if (!activeResp.IsSuccessStatusCode)
                    {
                        var finalErr = await activeResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        throw new InvalidOperationException($"Anthropic API 请求失败（HTTP {(int)activeResp.StatusCode}）：{TrimError(finalErr)}");
                    }
                }
            }
            catch
            {
                activeResp?.Dispose();
                throw;
            }

            using var resp = activeResp;

            var isSse = string.Equals(resp.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            var textBuilder = new StringBuilder();
            var turnThinking = new StringBuilder();
            var toolUses = new List<(string Id, string Name, string ArgsJson)>();
            JsonArray? assistantBlocksForHistory = null;

            if (isSse)
            {
                var blocks = new SortedDictionary<int, AnthropicStreamBlock>();
                var baseThink = cumulativeThinking.Length > 0 ? cumulativeThinking + "\n\n" : string.Empty;
                long inTok = 0, outTok = 0;

                using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line[5..].Trim();
                    if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;
                        var evType = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

                        if (evType == "message_start" && root.TryGetProperty("message", out var mObj) && mObj.TryGetProperty("usage", out var u1))
                        {
                            if (u1.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var iv)) inTok += iv;
                            if (u1.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov)) outTok += ov;
                        }
                        else if (evType == "message_delta" && root.TryGetProperty("usage", out var u2))
                        {
                            if (u2.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov)) outTok = Math.Max(outTok, ov);
                        }
                        else if (evType == "content_block_start" && root.TryGetProperty("content_block", out var cb))
                        {
                            var idx = root.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : blocks.Count;
                            var bType = cb.TryGetProperty("type", out var btp) ? btp.GetString() ?? "text" : "text";
                            var blk = new AnthropicStreamBlock
                            {
                                Type = bType,
                                Id = cb.TryGetProperty("id", out var idP) ? idP.GetString() ?? string.Empty : string.Empty,
                                Name = cb.TryGetProperty("name", out var nmP) ? nmP.GetString() ?? string.Empty : string.Empty,
                            };
                            if (bType == "text" && cb.TryGetProperty("text", out var initTxt) && initTxt.ValueKind == JsonValueKind.String)
                            {
                                blk.Text.Append(initTxt.GetString());
                                textBuilder.Append(initTxt.GetString());
                            }
                            else if (bType == "thinking" && cb.TryGetProperty("thinking", out var initTh) && initTh.ValueKind == JsonValueKind.String)
                            {
                                blk.Text.Append(initTh.GetString());
                                turnThinking.Append(initTh.GetString());
                            }
                            blocks[idx] = blk;
                        }
                        else if (evType == "content_block_delta" && root.TryGetProperty("delta", out var delta))
                        {
                            var idx = root.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var iv) ? iv : 0;
                            if (!blocks.TryGetValue(idx, out var blk))
                            {
                                blk = new AnthropicStreamBlock();
                                blocks[idx] = blk;
                            }
                            var dType = delta.TryGetProperty("type", out var dtp) ? dtp.GetString() : null;
                            if (dType == "text_delta" && delta.TryGetProperty("text", out var td) && td.ValueKind == JsonValueKind.String)
                            {
                                blk.Text.Append(td.GetString());
                                textBuilder.Append(td.GetString());
                            }
                            else if (dType == "thinking_delta" && delta.TryGetProperty("thinking", out var thd) && thd.ValueKind == JsonValueKind.String)
                            {
                                blk.Type = "thinking";
                                blk.Text.Append(thd.GetString());
                                turnThinking.Append(thd.GetString());
                            }
                            else if (dType == "signature_delta" && delta.TryGetProperty("signature", out var sig) && sig.ValueKind == JsonValueKind.String)
                            {
                                blk.Signature += sig.GetString();
                            }
                            else if (dType == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj) && pj.ValueKind == JsonValueKind.String)
                            {
                                blk.Type = "tool_use";
                                blk.JsonInput.Append(pj.GetString());
                            }

                            var liveThink = turnThinking.Length > 0
                                ? baseThink + turnThinking
                                : (baseThink.Length > 0 ? baseThink.TrimEnd() : null);
                            var activity = blk.Type == "tool_use"
                                ? $"正在准备工具调用 ({blk.Name})…"
                                : (textBuilder.Length > 0 ? "正在流式输出…" : "正在深度思考…");
                            progress.Report(new GroupChatUpdate(textBuilder.ToString(), activity, steps.ToArray(), liveThink));
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                if (inTok > 0 || outTok > 0) _onTokenUsage?.Invoke(request.Workspace.Id, inTok, outTok);
                if (turnThinking.Length > 0)
                {
                    if (cumulativeThinking.Length > 0) cumulativeThinking.AppendLine().AppendLine();
                    cumulativeThinking.Append(turnThinking);
                }

                assistantBlocksForHistory = new JsonArray();
                foreach (var blk in blocks.Values)
                {
                    if (blk.Type == "thinking")
                    {
                        var thNode = new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = blk.Text.ToString(),
                        };
                        if (!string.IsNullOrEmpty(blk.Signature)) thNode["signature"] = blk.Signature;
                        assistantBlocksForHistory.Add(thNode);
                    }
                    else if (blk.Type == "tool_use")
                    {
                        var rawJson = blk.JsonInput.Length > 0 ? blk.JsonInput.ToString() : "{}";
                        var id = string.IsNullOrEmpty(blk.Id) ? Guid.NewGuid().ToString("N") : blk.Id;
                        var name = string.IsNullOrEmpty(blk.Name) ? "tool" : blk.Name;
                        toolUses.Add((id, name, rawJson));
                        JsonNode? parsedInput = null;
                        try { parsedInput = JsonNode.Parse(rawJson); } catch { parsedInput = new JsonObject(); }
                        assistantBlocksForHistory.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = id,
                            ["name"] = name,
                            ["input"] = parsedInput,
                        });
                    }
                    else if (blk.Text.Length > 0)
                    {
                        assistantBlocksForHistory.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = blk.Text.ToString(),
                        });
                    }
                }
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    var inTok = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var iv) ? iv : 0L;
                    var outTok = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov) ? ov : 0L;
                    if (inTok > 0 || outTok > 0) _onTokenUsage?.Invoke(request.Workspace.Id, inTok, outTok);
                }

                var contentArr = root.GetProperty("content");
                assistantBlocksForHistory = JsonNode.Parse(contentArr.GetRawText())?.AsArray();
                foreach (var block in contentArr.EnumerateArray())
                {
                    var bType = block.GetProperty("type").GetString();
                    if (bType == "thinking" && block.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.String)
                    {
                        if (cumulativeThinking.Length > 0) cumulativeThinking.AppendLine().AppendLine();
                        cumulativeThinking.Append(th.GetString());
                    }
                    else if (bType == "text" && block.TryGetProperty("text", out var t))
                    {
                        textBuilder.Append(t.GetString());
                    }
                    else if (bType == "tool_use")
                    {
                        var id = block.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                        var name = block.GetProperty("name").GetString() ?? "tool";
                        var inputJson = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                        toolUses.Add((id, name, inputJson));
                    }
                }
            }

            steps[^1] = steps[^1] with { Status = "completed" };
            var textSoFar = textBuilder.ToString().Trim();
            var currentThinking = cumulativeThinking.Length > 0 ? cumulativeThinking.ToString() : null;

            if (allowTools && toolUses.Count > 0)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = assistantBlocksForHistory,
                });

                var toolResults = new JsonArray();
                foreach (var tu in toolUses)
                {
                    var step = BuildLocalToolStep($"tool-{++stepSeq}", tu.Name, tu.ArgsJson, "running");
                    steps.Add(step);
                    progress.Report(new GroupChatUpdate(textSoFar, step.Title, steps.ToArray(), currentThinking));

                    var result = await ExecuteLocalToolSafeAsync(tu.Name, tu.ArgsJson, request.WorkingDirectory, writable, token).ConfigureAwait(false);
                    var failed = result.StartsWith("ExitCode:", StringComparison.Ordinal) && !result.StartsWith("ExitCode: 0", StringComparison.Ordinal);
                    steps[^1] = step with { Status = failed ? "failed" : "completed" };

                    toolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = tu.Id,
                        ["content"] = result,
                    });
                }

                messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
                continue;
            }

            if (string.IsNullOrWhiteSpace(textSoFar))
                textSoFar = "（Claude 已完成工具操作。）";
            progress.Report(new GroupChatUpdate(textSoFar, "已完成", steps.ToArray(), currentThinking));
            return textSoFar;
        }

        throw new InvalidOperationException($"Anthropic Agent 工具循环超过 {MaxToolIterations} 轮上限。");
    }

    private async Task<string> RunGeminiLoopAsync(
        string baseUrl,
        string model,
        string? apiKey,
        string systemPrompt,
        GroupChatRun request,
        bool allowTools,
        bool writable,
        List<ChatExecutionStep> steps,
        IProgress<GroupChatUpdate> progress,
        CancellationToken token)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var modelPath = model.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? model : "models/" + model;
        var streamEndpoint = $"{trimmed}/v1beta/{modelPath}:streamGenerateContent?alt=sse";
        var syncEndpoint = $"{trimmed}/v1beta/{modelPath}:generateContent";

        var contents = BuildGeminiInitialContents(request);

        var tools = allowTools ? BuildGeminiToolsJson(writable) : null;
        var supportsThinkingConfig = model.Contains("2.5", StringComparison.OrdinalIgnoreCase)
            || model.Contains('3', StringComparison.Ordinal)
            || model.Contains('4', StringComparison.Ordinal)
            || model.Contains("thinking", StringComparison.OrdinalIgnoreCase);
        var stepSeq = 0;
        var cumulativeThinking = new StringBuilder();

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            token.ThrowIfCancellationRequested();
            steps.Add(new ChatExecutionStep($"gem-req-{++stepSeq}", "thinking", $"请求 Gemini ({model}) 第 {iteration + 1} 轮", "running"));
            progress.Report(new GroupChatUpdate(string.Empty, $"正在连接 {model}…", steps.ToArray(), cumulativeThinking.Length > 0 ? cumulativeThinking.ToString() : null));

            var payload = new JsonObject
            {
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = systemPrompt } },
                },
                ["contents"] = contents.DeepClone(),
            };
            if (tools is not null) payload["tools"] = tools.DeepClone();
            if (allowTools && (supportsThinkingConfig || !string.IsNullOrWhiteSpace(request.Agent.ReasoningEffort)))
            {
                payload["generationConfig"] = new JsonObject
                {
                    ["thinkingConfig"] = BuildGeminiThinkingConfig(model, request.Agent.ReasoningEffort),
                };
            }

            var targetUrl = allowTools ? streamEndpoint : syncEndpoint;
            HttpResponseMessage? activeResp = null;
            try
            {
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, targetUrl)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(apiKey))
                    httpReq.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

                activeResp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!activeResp.IsSuccessStatusCode)
                {
                    var errBody = await activeResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if ((int)activeResp.StatusCode == 400 && errBody.Contains("thinking", StringComparison.OrdinalIgnoreCase) && payload.ContainsKey("generationConfig"))
                    {
                        activeResp.Dispose();
                        payload.Remove("generationConfig");
                        using var retryReq = new HttpRequestMessage(HttpMethod.Post, targetUrl)
                        {
                            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                        };
                        if (!string.IsNullOrWhiteSpace(apiKey))
                            retryReq.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                        activeResp = await _http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    }
                    if (!activeResp.IsSuccessStatusCode)
                    {
                        var finalErr = await activeResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        throw new InvalidOperationException($"Gemini API 请求失败（HTTP {(int)activeResp.StatusCode}）：{TrimError(finalErr)}");
                    }
                }
            }
            catch
            {
                activeResp?.Dispose();
                throw;
            }

            using var resp = activeResp;

            var isSse = string.Equals(resp.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            var textBuilder = new StringBuilder();
            var turnThinking = new StringBuilder();
            var fnCalls = new List<(string Name, string ArgsJson)>();
            var preservedModelParts = new JsonArray();
            long inTok = 0, outTok = 0;

            void ConsumeGeminiChunk(JsonElement root, string baseThink)
            {
                if (root.TryGetProperty("usageMetadata", out var um) && um.ValueKind == JsonValueKind.Object)
                {
                    if (um.TryGetProperty("promptTokenCount", out var pt) && pt.TryGetInt64(out var pv)) inTok = Math.Max(inTok, pv);
                    if (um.TryGetProperty("candidatesTokenCount", out var ct) && ct.TryGetInt64(out var cv)) outTok = Math.Max(outTok, cv);
                }

                if (!root.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
                    return;

                if (!cands[0].TryGetProperty("content", out var modelContent) || !modelContent.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var part in parts.EnumerateArray())
                {
                    preservedModelParts.Add(JsonNode.Parse(part.GetRawText()));
                    var isThought = part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True;
                    if (isThought)
                    {
                        if (part.TryGetProperty("text", out var thTxt) && thTxt.ValueKind == JsonValueKind.String)
                            turnThinking.Append(thTxt.GetString());
                        continue;
                    }
                    if (part.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                        textBuilder.Append(txt.GetString());
                    if (part.TryGetProperty("functionCall", out var fc) && fc.ValueKind == JsonValueKind.Object)
                    {
                        var name = fc.GetProperty("name").GetString() ?? "tool";
                        var argsJson = fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                        fnCalls.Add((name, argsJson));
                    }
                }

                var liveThink = turnThinking.Length > 0
                    ? baseThink + turnThinking
                    : (baseThink.Length > 0 ? baseThink.TrimEnd() : null);
                var activity = fnCalls.Count > 0
                    ? $"准备调用工具 ({fnCalls[^1].Name})…"
                    : (textBuilder.Length > 0 ? "正在流式输出…" : "正在深度思考…");
                progress.Report(new GroupChatUpdate(textBuilder.ToString(), activity, steps.ToArray(), liveThink));
            }

            var baseThinkingPrefix = cumulativeThinking.Length > 0 ? cumulativeThinking + "\n\n" : string.Empty;
            if (isSse)
            {
                using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line[5..].Trim();
                    if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        ConsumeGeminiChunk(doc.RootElement, baseThinkingPrefix);
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
                    throw new InvalidOperationException("Gemini API 未返回有效候选回复。");
                ConsumeGeminiChunk(doc.RootElement, baseThinkingPrefix);
            }

            if (inTok > 0 || outTok > 0) _onTokenUsage?.Invoke(request.Workspace.Id, inTok, outTok);
            if (turnThinking.Length > 0)
            {
                if (cumulativeThinking.Length > 0) cumulativeThinking.AppendLine().AppendLine();
                cumulativeThinking.Append(turnThinking);
            }

            steps[^1] = steps[^1] with { Status = "completed" };
            var textSoFar = textBuilder.ToString().Trim();
            var currentThinking = cumulativeThinking.Length > 0 ? cumulativeThinking.ToString() : null;

            if (allowTools && fnCalls.Count > 0)
            {
                contents.Add(new JsonObject
                {
                    ["role"] = "model",
                    ["parts"] = preservedModelParts,
                });

                var responseParts = new JsonArray();
                foreach (var fc in fnCalls)
                {
                    var step = BuildLocalToolStep($"tool-{++stepSeq}", fc.Name, fc.ArgsJson, "running");
                    steps.Add(step);
                    progress.Report(new GroupChatUpdate(textSoFar, step.Title, steps.ToArray(), currentThinking));

                    var toolOutput = await ExecuteLocalToolSafeAsync(fc.Name, fc.ArgsJson, request.WorkingDirectory, writable, token).ConfigureAwait(false);
                    var failed = toolOutput.StartsWith("ExitCode:", StringComparison.Ordinal) && !toolOutput.StartsWith("ExitCode: 0", StringComparison.Ordinal);
                    steps[^1] = step with { Status = failed ? "failed" : "completed" };

                    responseParts.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = fc.Name,
                            ["response"] = new JsonObject { ["output"] = toolOutput },
                        },
                    });
                }

                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = responseParts });
                continue;
            }

            if (string.IsNullOrWhiteSpace(textSoFar))
                textSoFar = "（Gemini 已完成本地工具调用。）";
            progress.Report(new GroupChatUpdate(textSoFar, "已完成", steps.ToArray(), currentThinking));
            return textSoFar;
        }

        throw new InvalidOperationException($"Gemini Agent 工具循环超过 {MaxToolIterations} 轮上限。");
    }

    private static ChatExecutionStep BuildLocalToolStep(string id, string toolName, string argsJson, string status)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var rawPath = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (toolName == "read_file" && !string.IsNullOrWhiteSpace(rawPath))
                {
                    var baseName = Path.GetFileName(rawPath);
                    var s = root.TryGetProperty("start_line", out var sl) && sl.TryGetInt32(out var sv) ? sv : (int?)null;
                    var e = root.TryGetProperty("end_line", out var el) && el.TryGetInt32(out var ev) ? ev : (int?)null;
                    var meta = s is not null ? (e is not null ? $"#L{s}-{e}" : $"#L{s}") : null;
                    return new ChatExecutionStep(id, "read", $"Analyzed {baseName}", status, baseName, meta, rawPath);
                }

                if (toolName == "edit_file" && !string.IsNullOrWhiteSpace(rawPath))
                {
                    var baseName = Path.GetFileName(rawPath);
                    var oldLines = root.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Split('\n').Length : 0;
                    var newLines = root.TryGetProperty("replacement", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()!.Split('\n').Length : 0;
                    var meta = (oldLines > 0 || newLines > 0) ? $"+{newLines} -{oldLines}" : null;
                    return new ChatExecutionStep(id, "edit", $"Edited {baseName}", status, baseName, meta, rawPath);
                }

                if (toolName == "write_file" && !string.IsNullOrWhiteSpace(rawPath))
                {
                    var baseName = Path.GetFileName(rawPath);
                    var lines = root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()!.Split('\n').Length : 0;
                    var meta = lines > 0 ? $"+{lines} -0" : null;
                    return new ChatExecutionStep(id, "write", $"Created {baseName}", status, baseName, meta, rawPath);
                }

                if (toolName == "list_files")
                {
                    var dir = string.IsNullOrWhiteSpace(rawPath) ? "." : rawPath!;
                    return new ChatExecutionStep(id, "search", $"Listed {dir}", status, dir, null, dir);
                }

                if (toolName == "search_files")
                {
                    var q = root.TryGetProperty("query", out var qp) && qp.ValueKind == JsonValueKind.String ? qp.GetString() ?? "pattern" : "pattern";
                    return new ChatExecutionStep(id, "search", $"Searched \"{q}\"", status, q, null, q);
                }

                if (toolName == "run_command" && root.TryGetProperty("command", out var cmdP) && cmdP.ValueKind == JsonValueKind.String)
                {
                    var cmd = cmdP.GetString() ?? "command";
                    var compact = cmd.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    return new ChatExecutionStep(id, "command", compact, status, compact, null, cmd);
                }
            }
        }
        catch { }
        return new ChatExecutionStep(id, "tool", toolName, status, toolName);
    }

    private static string MapOpenAiReasoningEffort(string rawEffort) => rawEffort.Trim().ToLowerInvariant() switch
    {
        "none" => "none",
        "minimal" => "minimal",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "xhigh" or "max" or "ultra" => "xhigh",
        _ => "medium",
    };

    private static int? MapAnthropicThinkingBudget(string? rawEffort) => rawEffort?.Trim().ToLowerInvariant() switch
    {
        "minimal" or "low" => 2048,
        "medium" => 8192,
        "high" => 16384,
        "xhigh" or "max" or "ultra" => 24576,
        _ => null,
    };

    private static JsonObject BuildGeminiThinkingConfig(string model, string? rawEffort)
    {
        var cfg = new JsonObject { ["includeThoughts"] = true };
        if (string.IsNullOrWhiteSpace(rawEffort))
            return cfg;

        var eff = rawEffort.Trim().ToLowerInvariant();
        var isGemini25 = model.Contains("2.5", StringComparison.OrdinalIgnoreCase);
        if (isGemini25)
        {
            cfg["thinkingBudget"] = eff switch
            {
                "none" => 0,
                "minimal" or "low" => 1024,
                "medium" => 8192,
                _ => 24576,
            };
        }
        else
        {
            cfg["thinkingLevel"] = eff switch
            {
                "none" or "minimal" => "MINIMAL",
                "low" => "LOW",
                "medium" => "MEDIUM",
                _ => "HIGH",
            };
        }
        return cfg;
    }

    private static string NormalizeOpenAiEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return trimmed + "/chat/completions";
    }

    private static string TrimError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "空响应";
        var single = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length > 280 ? single[..280] + "…" : single;
    }

    private static async Task<string> ExecuteLocalToolSafeAsync(
        string toolName,
        string argsJson,
        string workspaceRoot,
        bool writable,
        CancellationToken token)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var args = doc.RootElement;

            switch (toolName)
            {
                case "list_files":
                {
                    var rel = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : ".";
                    var recursive = args.TryGetProperty("recursive", out var recProp) && recProp.ValueKind is JsonValueKind.True or JsonValueKind.False && recProp.GetBoolean();
                    var targetDir = ResolveContainedWorkspacePath(workspaceRoot, rel);
                    if (!Directory.Exists(targetDir)) return $"目录不存在：{rel}";
                    var entries = new List<string>();
                    if (!recursive)
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(targetDir).Take(200))
                        {
                            var name = Path.GetFileName(entry);
                            if (IsIgnoredDirectoryName(name)) continue;
                            var isDir = Directory.Exists(entry);
                            entries.Add((isDir ? "[DIR]  " : "[FILE] ") + Path.GetRelativePath(workspaceRoot, entry));
                        }
                    }
                    else
                    {
                        EnumerateWorkspaceFilesRecursive(workspaceRoot, targetDir, entries, maxEntries: 250);
                    }
                    return entries.Count == 0 ? "（目录为空）" : string.Join('\n', entries);
                }

                case "search_files":
                {
                    var query = RequireArg(args, "query");
                    var rel = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : ".";
                    var isRegex = args.TryGetProperty("is_regex", out var rxProp) && rxProp.ValueKind is JsonValueKind.True or JsonValueKind.False && rxProp.GetBoolean();
                    var targetDir = ResolveContainedWorkspacePath(workspaceRoot, rel);
                    if (!Directory.Exists(targetDir) && !File.Exists(targetDir)) return $"搜索路径不存在：{rel}";
                    return await SearchWorkspaceFilesAsync(workspaceRoot, targetDir, query, isRegex, token).ConfigureAwait(false);
                }

                case "read_file":
                {
                    var rel = RequireArg(args, "path");
                    var targetFile = ResolveContainedWorkspacePath(workspaceRoot, rel);
                    if (!File.Exists(targetFile)) return $"文件不存在：{rel}";
                    var startLine = args.TryGetProperty("start_line", out var sl) && sl.TryGetInt32(out var sv) ? Math.Max(1, sv) : 1;
                    var endLine = args.TryGetProperty("end_line", out var el) && el.TryGetInt32(out var ev) ? Math.Max(startLine, ev) : startLine + 249;
                    var allLines = await File.ReadAllLinesAsync(targetFile, token).ConfigureAwait(false);
                    var sb = new StringBuilder();
                    var upper = Math.Min(allLines.Length, Math.Min(endLine, startLine + 399));
                    for (var i = startLine; i <= upper; i++)
                        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{i}: {allLines[i - 1]}");
                    if (allLines.Length > upper)
                        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"... (共 {allLines.Length} 行，已显示 {startLine}-{upper})");
                    return sb.ToString();
                }

                case "write_file":
                {
                    if (!writable) return "权限拒绝：当前会话处于 Ask 只读模式，不能写入文件。";
                    var rel = RequireArg(args, "path");
                    var content = args.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                    var targetFile = ResolveContainedWorkspacePath(workspaceRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                    ProfilePathSafety.ThrowIfReparsePath(targetFile);
                    await File.WriteAllTextAsync(targetFile, content, new UTF8Encoding(false), token).ConfigureAwait(false);
                    return $"已成功写入文件：{Path.GetRelativePath(workspaceRoot, targetFile)}（{content.Length} 字符）";
                }

                case "edit_file":
                {
                    if (!writable) return "权限拒绝：当前会话处于 Ask 只读模式，不能修改文件。";
                    var rel = RequireArg(args, "path");
                    var targetText = args.TryGetProperty("target", out var tProp) && tProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tProp.GetString())
                        ? tProp.GetString()!
                        : RequireArg(args, "old_text");
                    var replacement = args.TryGetProperty("replacement", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? string.Empty
                        : (args.TryGetProperty("new_text", out var nr) && nr.ValueKind == JsonValueKind.String ? nr.GetString() ?? string.Empty : string.Empty);
                    var replaceAll = args.TryGetProperty("replace_all", out var ra) && ra.ValueKind is JsonValueKind.True or JsonValueKind.False && ra.GetBoolean();
                    var targetFile = ResolveContainedWorkspacePath(workspaceRoot, rel);
                    if (!File.Exists(targetFile)) return $"文件不存在：{rel}";
                    var current = await File.ReadAllTextAsync(targetFile, token).ConfigureAwait(false);

                    // Preserve original Windows CRLF vs LF style while matching CRLF/LF agnostically
                    var usesCrlf = current.Contains("\r\n", StringComparison.Ordinal);
                    var normCurrent = current.Replace("\r\n", "\n", StringComparison.Ordinal);
                    var normTarget = targetText.Replace("\r\n", "\n", StringComparison.Ordinal);
                    var normReplacement = replacement.Replace("\r\n", "\n", StringComparison.Ordinal);

                    var firstIdx = normCurrent.IndexOf(normTarget, StringComparison.Ordinal);
                    if (firstIdx < 0)
                        return "替换失败：未在文件中找到匹配的 target 片段（已自动忽略 CRLF/LF 差异），请先调用 read_file 核对精确缩进与上下文。";

                    if (!replaceAll)
                    {
                        var secondIdx = normCurrent.IndexOf(normTarget, firstIdx + Math.Max(1, normTarget.Length), StringComparison.Ordinal);
                        if (secondIdx >= 0)
                            return "替换拦截：target 在文件中出现多处，请在 target 中补充前后 2~3 行唯一上下文，或显式传入 replace_all: true。";
                    }

                    var normUpdated = replaceAll
                        ? normCurrent.Replace(normTarget, normReplacement, StringComparison.Ordinal)
                        : string.Concat(normCurrent.AsSpan(0, firstIdx), normReplacement, normCurrent.AsSpan(firstIdx + normTarget.Length));

                    var finalOutput = usesCrlf ? normUpdated.Replace("\n", "\r\n", StringComparison.Ordinal) : normUpdated;
                    await File.WriteAllTextAsync(targetFile, finalOutput, new UTF8Encoding(false), token).ConfigureAwait(false);
                    return $"已成功修改文件：{Path.GetRelativePath(workspaceRoot, targetFile)}";
                }

                case "run_command":
                {
                    if (!writable) return "权限拒绝：当前会话处于 Ask 只读模式，不能执行命令。";
                    var cmd = RequireArg(args, "command");
                    return await RunHeadlessCommandAsync(cmd, workspaceRoot, token).ConfigureAwait(false);
                }

                default:
                    return $"未知工具：{toolName}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"工具执行异常：{ex.Message}";
        }
    }

    private static string ResolveContainedWorkspacePath(string workspaceRoot, string? userPath)
    {
        var rootFull = Path.GetFullPath(workspaceRoot);
        ProfilePathSafety.ThrowIfReparsePath(rootFull);
        var candidate = string.IsNullOrWhiteSpace(userPath) || userPath == "."
            ? rootFull
            : Path.IsPathRooted(userPath)
                ? Path.GetFullPath(userPath)
                : Path.GetFullPath(Path.Combine(rootFull, userPath));

        var rootWithSep = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安全拦截：不允许访问当前项目工作区目录以外的文件。");
        }

        ProfilePathSafety.ThrowIfReparsePath(candidate);
        return candidate;
    }

    private static async Task<string> RunHeadlessCommandAsync(string command, string workingDirectory, CancellationToken token)
    {
        var psi = new ProcessStartInfo(GitWorktreeService.FindPreferredPowerShellExecutable())
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动后台 PowerShell。");
        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(budget.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(budget.Token);
            await proc.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            var combined = $"ExitCode: {proc.ExitCode}\n{(await stdoutTask.ConfigureAwait(false)).Trim()}\n{(await stderrTask.ConfigureAwait(false)).Trim()}".Trim();
            return combined.Length > 12_000 ? combined[..12_000] + "\n...(输出过长已截断)" : combined;
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static string RequireArg(JsonElement args, string name) =>
        args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.GetString())
            ? prop.GetString()!
            : throw new InvalidOperationException($"缺少参数 {name}。");

    private static bool IsIgnoredDirectoryName(string name) =>
        name is ".git" or "node_modules" or "bin" or "obj" or "packages" or ".vs" or "dist" or "artifacts" or "__pycache__" or ".venv" or "target";

    private static void EnumerateWorkspaceFilesRecursive(string workspaceRoot, string currentDir, List<string> results, int maxEntries)
    {
        if (results.Count >= maxEntries) return;
        try
        {
            if ((File.GetAttributes(currentDir) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                results.Add("[FILE] " + Path.GetRelativePath(workspaceRoot, file));
                if (results.Count >= maxEntries) return;
            }
            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                if (IsIgnoredDirectoryName(Path.GetFileName(subDir))) continue;
                EnumerateWorkspaceFilesRecursive(workspaceRoot, subDir, results, maxEntries);
                if (results.Count >= maxEntries) return;
            }
        }
        catch { }
    }

    private static async Task<string> SearchWorkspaceFilesAsync(
        string workspaceRoot,
        string targetPath,
        string query,
        bool isRegex,
        CancellationToken token)
    {
        Regex? regex = null;
        if (isRegex)
        {
            try { regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
            catch (ArgumentException ex) { return $"正则表达式语法无效：{ex.Message}"; }
        }

        var filesToScan = new List<string>();
        void Collect(string dir)
        {
            if (filesToScan.Count >= 600) return;
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".exe" or ".dll" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".zip" or ".woff" or ".woff2" or ".ttf")
                        continue;
                    if (new FileInfo(f).Length > 1024 * 1024) continue;
                    filesToScan.Add(f);
                    if (filesToScan.Count >= 600) return;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (IsIgnoredDirectoryName(Path.GetFileName(sub))) continue;
                    Collect(sub);
                    if (filesToScan.Count >= 600) return;
                }
            }
            catch { }
        }

        if (File.Exists(targetPath)) filesToScan.Add(targetPath);
        else Collect(targetPath);

        var matches = new List<string>();
        foreach (var file in filesToScan)
        {
            token.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, token).ConfigureAwait(false); }
            catch { continue; }

            var relPath = Path.GetRelativePath(workspaceRoot, file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var hit = regex is not null
                    ? SafeIsMatch(regex, line)
                    : line.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (hit)
                {
                    var snippet = line.Length > 180 ? line[..180] + "…" : line;
                    matches.Add($"{relPath}:{i + 1}: {snippet.Trim()}");
                    if (matches.Count >= 80)
                    {
                        matches.Add("... (匹配超过 80 处，请缩小 path 或细化 query)");
                        return string.Join('\n', matches);
                    }
                }
            }
        }

        return matches.Count == 0 ? $"未找到匹配项：{query}" : string.Join('\n', matches);
    }

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static JsonArray BuildToolSpecs(bool writable)
    {
        var specs = new JsonArray
        {
            MakeSpec("list_files", "列出工作区指定子目录下的文件与文件夹（支持 recursive 递归扫描项目结构）。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "相对项目根目录的路径，默认为 '.'" },
                    ["recursive"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否递归列出子目录文件（自动忽略 node_modules/bin/obj/.git）" },
                },
            }),
            MakeSpec("search_files", "在工作区代码文件中快速搜索关键字或正则表达式并返回精确文件路径与行号。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "要搜索的关键字或正则表达式" },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "可选子目录或文件路径，默认为 '.'" },
                    ["is_regex"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否按正则表达式匹配，默认 false" },
                },
                ["required"] = new JsonArray("query"),
            }),
            MakeSpec("read_file", "读取工作区内指定文件的行号内容。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "相对项目根目录的文件路径" },
                    ["start_line"] = new JsonObject { ["type"] = "integer", ["description"] = "起始行号（从 1 开始）" },
                    ["end_line"] = new JsonObject { ["type"] = "integer", ["description"] = "结束行号" },
                },
                ["required"] = new JsonArray("path"),
            }),
        };

        if (writable)
        {
            specs.Add(MakeSpec("write_file", "在工作区内新建或覆写文件。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "相对项目根目录的文件路径" },
                    ["content"] = new JsonObject { ["type"] = "string", ["description"] = "完整文件内容" },
                },
                ["required"] = new JsonArray("path", "content"),
            }));

            specs.Add(MakeSpec("edit_file", "在工作区现有文件中将精确匹配的 target 替换为 replacement（自动兼容 CRLF/LF）。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "相对项目根目录的文件路径" },
                    ["target"] = new JsonObject { ["type"] = "string", ["description"] = "要替换的原文本片段（包含精确缩进与唯一上下文）" },
                    ["replacement"] = new JsonObject { ["type"] = "string", ["description"] = "替换后的新文本片段" },
                    ["replace_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "当 target 出现多处时是否全部替换，默认 false" },
                },
                ["required"] = new JsonArray("path", "target", "replacement"),
            }));

            specs.Add(MakeSpec("run_command", "在项目目录下静默无窗执行构建、测试或 Git 命令。", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command"] = new JsonObject { ["type"] = "string", ["description"] = "PowerShell 命令行字符串" },
                },
                ["required"] = new JsonArray("command"),
            }));
        }

        return specs;
    }

    private static JsonObject MakeSpec(string name, string description, JsonObject parameters) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["parameters"] = parameters,
    };

    private static JsonArray BuildOpenAiToolsJson(bool writable)
    {
        var arr = new JsonArray();
        foreach (var spec in BuildToolSpecs(writable))
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = spec!.DeepClone(),
            });
        }
        return arr;
    }

    private static JsonArray BuildAnthropicToolsJson(bool writable)
    {
        var arr = new JsonArray();
        foreach (var spec in BuildToolSpecs(writable))
        {
            var obj = spec!.AsObject();
            arr.Add(new JsonObject
            {
                ["name"] = obj["name"]!.GetValue<string>(),
                ["description"] = obj["description"]!.GetValue<string>(),
                ["input_schema"] = obj["parameters"]!.DeepClone(),
            });
        }
        return arr;
    }

    private static JsonArray BuildGeminiToolsJson(bool writable) =>
        new()
        {
            new JsonObject
            {
                ["functionDeclarations"] = BuildToolSpecs(writable),
            },
        };
}
