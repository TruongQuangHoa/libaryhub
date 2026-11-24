
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LibraryHub.Services.Chat
{
    public class GeminiChatService : IGeminiChatService
    {
        private readonly HttpClient _httpClient;
        private readonly ChatbotOptions _options;

        public GeminiChatService(
            HttpClient httpClient,
            IOptions<ChatbotOptions> options)
        {
            _httpClient = httpClient;
            _options = options.Value;
        }

        public async Task<string> SendMessageAsync(
            string message,
            string role,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "Vui lòng nhập nội dung câu hỏi.";

            if (string.IsNullOrWhiteSpace(_options.ApiKey))
                throw new InvalidOperationException(
                    "Chưa cấu hình Gemini API Key.");

            if (string.IsNullOrWhiteSpace(_options.Model))
                throw new InvalidOperationException(
                    "Chưa cấu hình Gemini Model.");

            string systemInstruction = role switch
            {
                "Admin" =>
                    "Bạn là trợ lý AI của hệ thống quản lý thư viện LibraryHub. " +
                    "Bạn hỗ trợ quản trị viên về quản lý và vận hành thư viện. " +
                    "Trả lời bằng tiếng Việt, rõ ràng và chính xác.",

                "Librarian" =>
                    "Bạn là trợ lý AI dành cho thủ thư LibraryHub. " +
                    "Bạn hỗ trợ nghiệp vụ thư viện, quản lý sách, độc giả, " +
                    "mượn và trả sách. Trả lời bằng tiếng Việt.",

                "Patron" =>
                    "Bạn là trợ lý AI dành cho độc giả LibraryHub. " +
                    "Bạn hỗ trợ tìm hiểu sách và hướng dẫn sử dụng thư viện. " +
                    "Trả lời bằng tiếng Việt, thân thiện.",

                _ =>
                    "Bạn là trợ lý AI của LibraryHub. " +
                    "Hãy trả lời bằng tiếng Việt, hữu ích và dễ hiểu."
            };

            var requestData = new
            {
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = systemInstruction }
                    }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = message }
                        }
                    }
                }
            };

            string responseContent = string.Empty;
            bool success = false;

            // Tối đa 3 lần gửi, chỉ thử lại khi Gemini trả HTTP 503.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var httpRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"models/{_options.Model}:generateContent");

                httpRequest.Headers.Add(
                    "x-goog-api-key",
                    _options.ApiKey);

                httpRequest.Content = JsonContent.Create(requestData);

                try
                {
                    using var response = await _httpClient.SendAsync(
                        httpRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                    responseContent = await response.Content.ReadAsStringAsync(
                        cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        success = true;
                        break;
                    }

                    // Chỉ retry khi máy chủ Gemini đang quá tải.
                    if (response.StatusCode ==
                            System.Net.HttpStatusCode.ServiceUnavailable
                        && attempt < 3)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(attempt * 2),
                            cancellationToken);

                        continue;
                    }

                    throw new HttpRequestException(
                        $"Gemini API trả về lỗi {(int)response.StatusCode}: " +
                        responseContent,
                        null,
                        response.StatusCode);
                }
                catch (TaskCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout không retry để tránh người dùng phải chờ quá lâu.
                    throw new TimeoutException(
                        "Gemini API không phản hồi trong thời gian cho phép.");
                }
            }

            if (!success || string.IsNullOrWhiteSpace(responseContent))
            {
                throw new HttpRequestException(
                    "Gemini không trả về phản hồi hợp lệ.");
            }

            using JsonDocument document =
                JsonDocument.Parse(responseContent);

            if (!document.RootElement.TryGetProperty(
                    "candidates", out JsonElement candidates) ||
                candidates.GetArrayLength() == 0)
            {
                return "Gemini chưa tạo được câu trả lời. Bạn vui lòng thử lại.";
            }

            JsonElement candidate = candidates[0];

            if (!candidate.TryGetProperty(
                    "content", out JsonElement content) ||
                !content.TryGetProperty(
                    "parts", out JsonElement parts))
            {
                return "Không thể đọc nội dung phản hồi từ Gemini.";
            }

            string answer = string.Join(
                "\n",
                parts.EnumerateArray()
                    .Where(part => part.TryGetProperty("text", out _))
                    .Select(part => part.GetProperty("text").GetString())
                    .Where(text => !string.IsNullOrWhiteSpace(text)));

            return string.IsNullOrWhiteSpace(answer)
                ? "Gemini chưa trả về nội dung câu trả lời."
                : answer;
        }
    }
}
