
namespace LibraryHub.Services.Chat
{
    public interface IGeminiChatService
    {
        Task<string> SendMessageAsync(
            string message,
            string role,
            CancellationToken cancellationToken = default);
    }
}
