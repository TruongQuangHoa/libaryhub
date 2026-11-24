namespace LibraryHub.Services.Chat
{
    public class ChatbotOptions
    {
        public string ApiKey { get; set; } = string.Empty;

        public string Model { get; set; } = "gemini-3.8-flash";
    }
}