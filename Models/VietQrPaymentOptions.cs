namespace LibraryHub.Models
{
    public sealed class VietQrPaymentOptions
    {
        public string BankBin { get; set; } = "970415";
        public string AccountNumber { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string SePayApiToken { get; set; } = string.Empty;
        public string WebhookApiKey { get; set; } = string.Empty;
        public int CheckTimeoutSeconds { get; set; } = 300;
    }
}
