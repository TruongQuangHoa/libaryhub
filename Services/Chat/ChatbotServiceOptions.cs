namespace LibraryHub.Services.Chat
{
    public class ChatbotServiceOptions
    {
        /// <summary>
        /// Base URL của Chatbot Python (ví dụ: http://127.0.0.1:8010).
        /// Nếu để trống, hệ thống sẽ dùng GeminiChatService (C#) như hiện tại.
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Khóa nội bộ gửi kèm header X-Internal-Token khi ASP.NET proxy sang Python.
        /// Phải khớp CHATBOT_INTERNAL_API_KEY trong Chatbot/.env.
        /// </summary>
        public string InternalApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Timeout (giây) khi gọi Chatbot Python.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Đường dẫn proxy tải file export qua ASP.NET (trình duyệt gọi vào host web).
        /// </summary>
        public string ExportProxyPath { get; set; } = "/chat/exports";
    }
}
