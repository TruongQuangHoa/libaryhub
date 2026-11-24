using LibraryHub.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LibraryHub.Controllers
{
    [Authorize(Roles = "Patron,Admin,Librarian")]
    [Route("chat")]
    public class ChatController : Controller
    {
        private readonly IGeminiChatService _chatService;
        private readonly IPythonChatbotClient _pythonChatbotClient;
        private readonly ILogger<ChatController> _logger;

        public ChatController(
            IGeminiChatService chatService,
            IPythonChatbotClient pythonChatbotClient,
            ILogger<ChatController> logger)
        {
            _chatService = chatService;
            _pythonChatbotClient = pythonChatbotClient;
            _logger = logger;
        }

        [HttpPost("send")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Send(
            [FromBody] ChatRequest model,
            CancellationToken cancellationToken)
        {
            if (model == null ||
                string.IsNullOrWhiteSpace(model.Message) ||
                model.Message.Length > 2000)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Vui lòng nhập câu hỏi hợp lệ."
                });
            }

            var role = User.IsInRole("Admin") ? "Admin"
                : User.IsInRole("Librarian") ? "Librarian"
                : User.IsInRole("Patron") ? "Patron"
                : string.Empty;

            if (string.IsNullOrEmpty(role))
                return Forbid();

            // Ưu tiên Chatbot Python (pipeline AgentShield/ngữ cảnh/read-tools/4B/audit) khi được cấu hình.
            if (_pythonChatbotClient.IsEnabled)
            {
                var (success, statusCode, payload) = await _pythonChatbotClient.SendMessageAsync(
                    HttpContext,
                    model.Message,
                    role,
                    model.CheckpointId,
                    model.ApprovalToken,
                    model.PageContext,
                    model.InterfaceContext,
                    cancellationToken);

                if (statusCode.HasValue && statusCode.Value >= 500)
                {
                    return StatusCode(statusCode.Value, payload);
                }

                return Ok(payload);
            }

            try
            {
                var answer = await _chatService.SendMessageAsync(
                    model.Message,
                    role,
                    cancellationToken);

                return Ok(new
                {
                    success = true,
                    message = answer
                });
            }

            catch (HttpRequestException ex)
    when (ex.StatusCode ==
          System.Net.HttpStatusCode.ServiceUnavailable)
            {
                _logger.LogWarning(
                    ex,
                    "Gemini API is temporarily unavailable.");

                return StatusCode(503, new
                {
                    success = false,
                    message = "Gemini hiện đang quá tải. Hệ thống đã thử kết nối lại nhưng chưa thành công. Vui lòng thử lại sau ít phút."
                });
            }

            catch (HttpRequestException ex)
                when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(ex, "Gemini API rate limit exceeded.");

                return StatusCode(429, new
                {
                    success = false,
                    message = "ChatBot đã đạt giới hạn sử dụng miễn phí. Vui lòng thử lại sau."
                });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Gemini API returned an HTTP error.");

                return StatusCode(502, new
                {
                    success = false,
                    message = "Gemini API đang gặp lỗi hoặc cấu hình chưa chính xác. Vui lòng kiểm tra nhật ký máy chủ."
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Gemini configuration error.");

                return StatusCode(500, new
                {
                    success = false,
                    message = "ChatBot chưa được cấu hình đầy đủ. Vui lòng kiểm tra Gemini API Key và Model."
                });
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Gemini API request timed out.");

                return StatusCode(504, new
                {
                    success = false,
                    message = "Gemini phản hồi quá chậm. Vui lòng thử lại sau ít phút."
                });
            }

            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Gemini API request timed out.");

                return StatusCode(504, new
                {
                    success = false,
                    message = "Gemini phản hồi quá chậm hoặc mất kết nối mạng. Vui lòng thử lại sau ít phút."
                });
            }

            catch (Exception ex)
            {
                _logger.LogError(ex, "ChatBot processing failed.");

                return StatusCode(500, new
                {
                    success = false,
                    message = "Đã xảy ra lỗi khi xử lý câu hỏi. Vui lòng kiểm tra nhật ký máy chủ."
                });
            }
        }

        [HttpGet("exports/{filename}")]
        public async Task<IActionResult> DownloadExport(string filename, CancellationToken cancellationToken)
        {
            if (!_pythonChatbotClient.IsEnabled)
                return NotFound(new { success = false, message = "Dịch vụ Chatbot Python chưa được cấu hình." });

            // Chống path traversal: chỉ chấp nhận tên file đơn giản
            var safeName = Path.GetFileName(filename ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeName) || safeName != filename ||
                safeName.Contains("..") || safeName.Contains('/') || safeName.Contains('\\'))
            {
                return BadRequest(new { success = false, message = "Tên tệp không hợp lệ." });
            }

            var (success, statusCode, data, contentType, fileName) = await _pythonChatbotClient.GetExportAsync(safeName, cancellationToken);
            if (!success || data == null)
            {
                return StatusCode(statusCode ?? 404, new { success = false, message = "Không tìm thấy tệp báo cáo hoặc không thể tải tệp." });
            }

            return File(data, contentType ?? "application/octet-stream", fileName ?? safeName);
        }
    }

    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;

        // Hỗ trợ luồng 4B (Human-in-the-loop) khi UI/API gửi kèm thông tin phê duyệt
        public string? CheckpointId { get; set; }
        public string? ApprovalToken { get; set; }

        // Ngữ cảnh giao diện hiện tại (được widget gửi lên để chatbot có hành vi đặc thù theo trang)
        public string? PageContext { get; set; }
        public Dictionary<string, object?>? InterfaceContext { get; set; }
    }
}