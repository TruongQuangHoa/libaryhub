using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LibraryHub.Services.Chat
{
    public interface IPythonChatbotClient
    {
        bool IsEnabled { get; }
        Task<(bool Success, int? StatusCode, IDictionary<string, object?> Payload)> SendMessageAsync(
            HttpContext httpContext,
            string message,
            string role,
            string? checkpointId = null,
            string? approvalToken = null,
            string? pageContext = null,
            IDictionary<string, object?>? interfaceContext = null,
            CancellationToken cancellationToken = default);

        Task<(bool Success, int? StatusCode, byte[]? Data, string? ContentType, string? FileName)> GetExportAsync(
            string filename,
            CancellationToken cancellationToken = default);
    }

    public class PythonChatbotClient : IPythonChatbotClient
    {
        private readonly HttpClient _httpClient;
        private readonly ChatbotServiceOptions _options;
        private readonly ILogger<PythonChatbotClient> _logger;

        public PythonChatbotClient(
            HttpClient httpClient,
            IOptions<ChatbotServiceOptions> options,
            ILogger<PythonChatbotClient> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public bool IsEnabled =>
            !string.IsNullOrWhiteSpace(_options.BaseUrl);

        public async Task<(bool Success, int? StatusCode, IDictionary<string, object?> Payload)> SendMessageAsync(
            HttpContext httpContext,
            string message,
            string role,
            string? checkpointId = null,
            string? approvalToken = null,
            string? pageContext = null,
            IDictionary<string, object?>? interfaceContext = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
            {
                return (false, null, new Dictionary<string, object?>
                {
                    ["success"] = false,
                    ["message"] = "Python chatbot service chưa được cấu hình."
                });
            }

            var user = httpContext.User;
            int? userId = null;
            var idValue = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (int.TryParse(idValue, out var parsedId))
            {
                userId = parsedId;
            }

            var userName = user.Identity?.Name
                           ?? user.FindFirstValue(ClaimTypes.Name)
                           ?? user.FindFirstValue(ClaimTypes.Email);

            string sessionSegment;
            try
            {
                sessionSegment = httpContext.Session?.Id ?? Guid.NewGuid().ToString("N");
            }
            catch
            {
                sessionSegment = Guid.NewGuid().ToString("N");
            }

            var sessionId = userId.HasValue
                ? $"user-{userId.Value}-{role.ToLowerInvariant()}"
                : $"session-{sessionSegment}-{role.ToLowerInvariant()}";

            // Ưu tiên ngữ cảnh giao diện do widget gửi lên, fallback Referer/header/path
            var referer = httpContext.Request.Headers.Referer.ToString();
            var resolvedPageContext = !string.IsNullOrWhiteSpace(pageContext)
                ? pageContext
                : (!string.IsNullOrWhiteSpace(referer)
                    ? referer
                    : (httpContext.Request.Headers["X-Page-Context"].ToString() ?? httpContext.Request.Path.Value));

            var requestPayload = new Dictionary<string, object?>
            {
                ["message"] = message,
                ["role"] = role,
                ["user_id"] = userId,
                ["user_name"] = userName,
                ["session_id"] = sessionId,
                ["page_context"] = resolvedPageContext
            };

            if (interfaceContext != null && interfaceContext.Count > 0)
            {
                requestPayload["metadata"] = new Dictionary<string, object?>
                {
                    ["interface"] = interfaceContext
                };
            }

            if (!string.IsNullOrWhiteSpace(checkpointId))
                requestPayload["checkpoint_id"] = checkpointId.Trim();
            if (!string.IsNullOrWhiteSpace(approvalToken))
                requestPayload["approval_token"] = approvalToken.Trim();

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/send")
            {
                Content = JsonContent.Create(requestPayload)
            };

            if (!string.IsNullOrWhiteSpace(_options.InternalApiKey))
            {
                request.Headers.TryAddWithoutValidation("X-Internal-Token", _options.InternalApiKey);
            }

            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return (false, (int)response.StatusCode, new Dictionary<string, object?>
                    {
                        ["success"] = false,
                        ["message"] = "Chatbot Python không trả về nội dung."
                    });
                }

                try
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                    string msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? (m.GetString() ?? string.Empty)
                        : content;
                    if (string.IsNullOrWhiteSpace(msg))
                    {
                        msg = success
                            ? "Chatbot đã xử lý yêu cầu."
                            : "Chatbot Python trả về phản hồi không hợp lệ.";
                    }

                    // Viết lại URL export để trình duyệt tải qua ASP.NET proxy (Python port có thể không public)
                    msg = RewriteExportLinksInText(msg);

                    var payload = new Dictionary<string, object?>
                    {
                        ["success"] = success,
                        ["message"] = msg
                    };

                    if (root.TryGetProperty("export", out var exportEl) && exportEl.ValueKind == JsonValueKind.Object)
                    {
                        var export = new Dictionary<string, object?>();
                        foreach (var prop in exportEl.EnumerateObject())
                        {
                            export[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                ? prop.Value.GetString()
                                : prop.Value.ToString();
                        }
                        if (export.TryGetValue("url", out var urlObj) && urlObj is string urlStr)
                        {
                            export["url"] = RewriteExportUrl(urlStr);
                        }
                        payload["export"] = export;
                    }

                    if (root.TryGetProperty("requires_human_approval", out var rha))
                    {
                        payload["requires_human_approval"] = rha.ValueKind == JsonValueKind.True
                                                             || (rha.ValueKind == JsonValueKind.String && bool.TryParse(rha.GetString(), out var b) && b);
                    }
                    if (root.TryGetProperty("checkpoint_id", out var cp) && cp.ValueKind == JsonValueKind.String)
                    {
                        payload["checkpoint_id"] = cp.GetString();
                    }
                    if (root.TryGetProperty("intent", out var intent) && intent.ValueKind == JsonValueKind.String)
                    {
                        payload["intent"] = intent.GetString();
                    }
                    if (root.TryGetProperty("risk_level", out var risk) && risk.ValueKind == JsonValueKind.String)
                    {
                        payload["risk_level"] = risk.GetString();
                    }
                    if (root.TryGetProperty("audit_id", out var aid) && aid.ValueKind == JsonValueKind.String)
                    {
                        payload["audit_id"] = aid.GetString();
                    }
                    if (root.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
                    {
                        payload["sources"] = sourcesEl.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString())
                            .Where(x => x != null)
                            .ToList();
                    }

                    if (!response.IsSuccessStatusCode && !success)
                    {
                        return (false, (int)response.StatusCode, payload);
                    }

                    return (success, (int)response.StatusCode, payload);
                }
                catch (JsonException)
                {
                    var fallback = response.IsSuccessStatusCode
                        ? content
                        : $"Lỗi Chatbot Python ({(int)response.StatusCode}).";
                    return (response.IsSuccessStatusCode, (int)response.StatusCode, new Dictionary<string, object?>
                    {
                        ["success"] = response.IsSuccessStatusCode,
                        ["message"] = fallback
                    });
                }
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Python chatbot request timed out.");
                return (false, 504, new Dictionary<string, object?>
                {
                    ["success"] = false,
                    ["message"] = "Chatbot Python phản hồi quá chậm. Vui lòng thử lại sau."
                });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Cannot reach Python chatbot service at {BaseUrl}", _options.BaseUrl);
                return (false, 502, new Dictionary<string, object?>
                {
                    ["success"] = false,
                    ["message"] = "Không thể kết nối tới dịch vụ Chatbot Python. Vui lòng kiểm tra service Chatbot."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error when calling Python chatbot service.");
                return (false, 500, new Dictionary<string, object?>
                {
                    ["success"] = false,
                    ["message"] = "Đã xảy ra lỗi khi gọi dịch vụ Chatbot Python."
                });
            }
        }

        public async Task<(bool Success, int? StatusCode, byte[]? Data, string? ContentType, string? FileName)> GetExportAsync(
            string filename,
            CancellationToken cancellationToken = default)
        {
            if (!IsEnabled)
                return (false, null, null, null, null);

            var safeName = Path.GetFileName(filename ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeName) || safeName != filename)
                return (false, 400, null, null, null);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"exports/{Uri.EscapeDataString(safeName)}");
                // Static export không bắt buộc token trong Python, nhưng gửi kèm nếu có để nhất quán
                if (!string.IsNullOrWhiteSpace(_options.InternalApiKey))
                {
                    request.Headers.TryAddWithoutValidation("X-Internal-Token", _options.InternalApiKey);
                }

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, (int)response.StatusCode, null, null, null);
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                               ?? response.Content.Headers.ContentDisposition?.FileName
                               ?? safeName;
                fileName = fileName?.Trim('"');
                return (true, (int)response.StatusCode, bytes, contentType, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to proxy export file {FileName} from Python chatbot.", safeName);
                return (false, 502, null, null, null);
            }
        }

        private string RewriteExportUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url ?? string.Empty;

            // Chuẩn hóa: lấy filename từ URL /exports/<name>
            var m = Regex.Match(url, @"/exports/(?<name>[^/?#]+)", RegexOptions.IgnoreCase);
            if (!m.Success)
                return url;

            var name = m.Groups["name"].Value;
            var proxyBase = (_options.ExportProxyPath ?? "/chat/exports").TrimEnd('/');
            return $"{proxyBase}/{name}";
        }

        private string RewriteExportLinksInText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? string.Empty;

            var proxyBase = (_options.ExportProxyPath ?? "/chat/exports").TrimEnd('/');
            // Thay các markdown/link URL trỏ /exports/... (kể cả có host tuyệt đối của python)
            return Regex.Replace(
                text,
                @"(?<prefix>\]\()(?<url>https?://[^\s\)]*?/exports/(?<name>[^)\s?#]+)|/exports/(?<name2>[^)\s?#]+))(?<suffix>\))",
                m =>
                {
                    var name = m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["name2"].Value;
                    return m.Groups["prefix"].Value + $"{proxyBase}/{name}" + m.Groups["suffix"].Value;
                },
                RegexOptions.IgnoreCase);
        }
    }
}
