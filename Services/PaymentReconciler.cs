using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LibraryHub.Services
{
    public sealed class SePayWebhookDto
    {
        public long Id { get; set; }
        public string? Gateway { get; set; }
        public string? TransactionDate { get; set; } // "yyyy-MM-dd HH:mm:ss", giờ Việt Nam
        public string? AccountNumber { get; set; }
        public string? Code { get; set; }
        public string? Content { get; set; }
        public string? TransferType { get; set; }
        public decimal TransferAmount { get; set; }
        public decimal Accumulated { get; set; }
        public string? SubAccount { get; set; }
        public string? ReferenceCode { get; set; }
        public string? Description { get; set; }
    }

    public sealed class ReconcileResult
    {
        public bool IsDuplicate { get; set; }
        public bool IsUnmatched { get; set; }
        public tblPaymentHistory? Payment { get; set; }
        public tblUnmatchedBankTransaction? Unmatched { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public interface IPaymentReconciler
    {
        Task EnsureTablesAsync(CancellationToken ct = default);
        Task<ReconcileResult> HandleIncomingAsync(SePayWebhookDto dto, string? rawPayload, CancellationToken ct = default);
        Task<int> SyncFromSePayApiAsync(CancellationToken ct = default);
        bool IsSePayConfigured();
    }

    public sealed class PaymentReconciler : IPaymentReconciler
    {
        private const string ProviderSePay = "SePay";
        private readonly DataContext _context;
        private readonly IFeePaymentService _feePaymentService;
        private readonly IOptions<VietQrPaymentOptions> _options;
        private readonly ILogger<PaymentReconciler> _logger;

        public PaymentReconciler(
            DataContext context,
            IFeePaymentService feePaymentService,
            IOptions<VietQrPaymentOptions> options,
            ILogger<PaymentReconciler> logger)
        {
            _context = context;
            _feePaymentService = feePaymentService;
            _options = options;
            _logger = logger;
        }

        public bool IsSePayConfigured()
        {
            return !string.IsNullOrWhiteSpace(_options.Value.SePayApiToken) ||
                   !string.IsNullOrWhiteSpace(_options.Value.WebhookApiKey);
        }

        public async Task EnsureTablesAsync(CancellationToken ct = default)
        {
            const string createOrAlterTablesSql = @"
IF OBJECT_ID(N'[dbo].[tblPaymentHistory]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[tblPaymentHistory](
        [PaymentID] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [ReturnID] INT NOT NULL,
        [BorrowingID] INT NOT NULL,
        [PatronID] INT NULL,
        [PatronName] NVARCHAR(200) NULL,
        [Amount] DECIMAL(18,2) NOT NULL DEFAULT 0,
        [PaymentMethod] NVARCHAR(50) NOT NULL,
        [PaymentStatus] NVARCHAR(50) NOT NULL DEFAULT N'Đã thanh toán',
        [TransactionCode] NVARCHAR(100) NULL,
        [Provider] NVARCHAR(50) NULL,
        [ProviderTransactionId] NVARCHAR(100) NULL,
        [RawPayload] NVARCHAR(MAX) NULL,
        [TransactionContent] NVARCHAR(250) NULL,
        [BankName] NVARCHAR(100) NULL,
        [BankAccountNumber] NVARCHAR(50) NULL,
        [ConfirmedBy] NVARCHAR(200) NULL,
        [PaymentDate] DATETIME NOT NULL DEFAULT GETDATE()
    );
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.tblPaymentHistory', 'TransactionCode') IS NULL
        ALTER TABLE [dbo].[tblPaymentHistory] ADD [TransactionCode] NVARCHAR(100) NULL;
    IF COL_LENGTH('dbo.tblPaymentHistory', 'Provider') IS NULL
        ALTER TABLE [dbo].[tblPaymentHistory] ADD [Provider] NVARCHAR(50) NULL;
    IF COL_LENGTH('dbo.tblPaymentHistory', 'ProviderTransactionId') IS NULL
        ALTER TABLE [dbo].[tblPaymentHistory] ADD [ProviderTransactionId] NVARCHAR(100) NULL;
    IF COL_LENGTH('dbo.tblPaymentHistory', 'RawPayload') IS NULL
        ALTER TABLE [dbo].[tblPaymentHistory] ADD [RawPayload] NVARCHAR(MAX) NULL;
END

IF OBJECT_ID(N'[dbo].[tblUnmatchedBankTransaction]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[tblUnmatchedBankTransaction](
        [UnmatchedID] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Provider] NVARCHAR(50) NOT NULL DEFAULT N'SePay',
        [ProviderTransactionId] NVARCHAR(100) NULL,
        [ReferenceCode] NVARCHAR(100) NULL,
        [Gateway] NVARCHAR(100) NULL,
        [AccountNumber] NVARCHAR(50) NULL,
        [TransferAmount] DECIMAL(18,2) NOT NULL DEFAULT 0,
        [Content] NVARCHAR(500) NULL,
        [Reason] NVARCHAR(250) NULL,
        [RawPayload] NVARCHAR(MAX) NULL,
        [TransactionDate] DATETIME NOT NULL DEFAULT GETDATE(),
        [CreatedAt] DATETIME NOT NULL DEFAULT GETDATE(),
        [IsResolved] BIT NOT NULL DEFAULT 0
    );
END";

            // Tách batch riêng cho CREATE INDEX vì SQL Server kiểm tra tên cột mới ở compile-time cùng batch với ALTER TABLE
            const string createIndexesSql = @"
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_tblPaymentHistory_Provider_TxId'
      AND object_id = OBJECT_ID(N'[dbo].[tblPaymentHistory]')
)
BEGIN
    CREATE UNIQUE INDEX [UX_tblPaymentHistory_Provider_TxId]
    ON [dbo].[tblPaymentHistory]([Provider], [ProviderTransactionId])
    WHERE [Provider] IS NOT NULL AND [ProviderTransactionId] IS NOT NULL;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_tblUnmatchedBankTransaction_Provider_TxId'
      AND object_id = OBJECT_ID(N'[dbo].[tblUnmatchedBankTransaction]')
)
BEGIN
    CREATE UNIQUE INDEX [UX_tblUnmatchedBankTransaction_Provider_TxId]
    ON [dbo].[tblUnmatchedBankTransaction]([Provider], [ProviderTransactionId])
    WHERE [ProviderTransactionId] IS NOT NULL;
END";

            await _context.Database.ExecuteSqlRawAsync(createOrAlterTablesSql, ct);
            await _context.Database.ExecuteSqlRawAsync(createIndexesSql, ct);
        }

        public async Task<ReconcileResult> HandleIncomingAsync(SePayWebhookDto dto, string? rawPayload, CancellationToken ct = default)
        {
            await EnsureTablesAsync(ct);

            string providerTxId = dto.Id > 0
                ? dto.Id.ToString(CultureInfo.InvariantCulture)
                : (!string.IsNullOrWhiteSpace(dto.ReferenceCode) ? dto.ReferenceCode.Trim() : Guid.NewGuid().ToString("N"));

            // 1. Idempotent: kiểm tra trùng (Provider, ProviderTransactionId) ở cả 2 bảng
            var existingPayment = await _context.PaymentHistories
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Provider == ProviderSePay && p.ProviderTransactionId == providerTxId, ct);
            if (existingPayment != null)
            {
                return new ReconcileResult { IsDuplicate = true, Payment = existingPayment, Message = "Giao dịch đã được ghi nhận trước đó." };
            }

            var existingUnmatched = await _context.UnmatchedBankTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Provider == ProviderSePay && u.ProviderTransactionId == providerTxId, ct);
            if (existingUnmatched != null)
            {
                return new ReconcileResult { IsDuplicate = true, IsUnmatched = true, Unmatched = existingUnmatched, Message = "Giao dịch chưa đối soát đã tồn tại." };
            }

            DateTime txDate = ParseVietnamTransactionDate(dto.TransactionDate);
            string txCode = !string.IsNullOrWhiteSpace(dto.ReferenceCode)
                ? dto.ReferenceCode.Trim()
                : providerTxId;

            // 2. Kiểm tra AccountNumber khớp với tài khoản cấu hình
            var setting = await _feePaymentService.GetActiveSettingAsync();
            string expectedAccount = !string.IsNullOrWhiteSpace(_options.Value.AccountNumber)
                ? _options.Value.AccountNumber.Trim()
                : (setting.BankAccountNumber ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(expectedAccount) &&
                !string.IsNullOrWhiteSpace(dto.AccountNumber) &&
                !string.Equals(dto.AccountNumber.Trim(), expectedAccount, StringComparison.OrdinalIgnoreCase))
            {
                var unmatched = await SaveUnmatchedAsync(dto, providerTxId, txCode, txDate, rawPayload,
                    $"Số tài khoản ({dto.AccountNumber}) không khớp tài khoản cấu hình ({expectedAccount}).", ct);
                return new ReconcileResult { IsUnmatched = true, Unmatched = unmatched, Message = unmatched.Reason ?? string.Empty };
            }

            // 3. Chuẩn hóa nội dung chuyển khoản và trích xuất mã phiếu trả RT{id} chống trùng tiền tố (RT6 != RT62)
            string rawContent = dto.Content ?? dto.Description ?? string.Empty;
            var matchedReturnIds = ExtractReturnIds(rawContent);

            if (matchedReturnIds.Count == 0)
            {
                var unmatched = await SaveUnmatchedAsync(dto, providerTxId, txCode, txDate, rawPayload,
                    "Không tìm thấy mã phiếu trả (RT...) trong nội dung chuyển khoản.", ct);
                return new ReconcileResult { IsUnmatched = true, Unmatched = unmatched, Message = unmatched.Reason ?? string.Empty };
            }

            if (matchedReturnIds.Count > 1)
            {
                var unmatched = await SaveUnmatchedAsync(dto, providerTxId, txCode, txDate, rawPayload,
                    $"Nội dung chuyển khoản chứa nhiều mã phiếu trả ({string.Join(", ", matchedReturnIds.Select(x => "RT" + x))}), cần nhân viên đối soát.", ct);
                return new ReconcileResult { IsUnmatched = true, Unmatched = unmatched, Message = unmatched.Reason ?? string.Empty };
            }

            int returnId = matchedReturnIds[0];

            // 4. Tìm phiếu trả và ghi nhận trong 1 DB transaction
            using var dbTx = await _context.Database.BeginTransactionAsync(ct);
            try
            {
                var returnModel = await _context.Returns
                    .Include(r => r.borrowing)
                        .ThenInclude(b => b!.patron)
                    .FirstOrDefaultAsync(r => r.ReturnID == returnId, ct);

                if (returnModel == null)
                {
                    await dbTx.RollbackAsync(ct);
                    var unmatched = await SaveUnmatchedAsync(dto, providerTxId, txCode, txDate, rawPayload,
                        $"Không tìm thấy phiếu trả RT{returnId} trong hệ thống.", ct);
                    return new ReconcileResult { IsUnmatched = true, Unmatched = unmatched, Message = unmatched.Reason ?? string.Empty };
                }

                decimal expectedFine = returnModel.TotalFine < 0 ? 0m : returnModel.TotalFine;
                decimal alreadyReceived = await _context.PaymentHistories
                    .Where(p => p.ReturnID == returnId &&
                                (p.PaymentStatus == "Đã thanh toán" ||
                                 p.PaymentStatus == "Đã thu một phần" ||
                                 p.PaymentStatus == "Thu dư"))
                    .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

                decimal remaining = Math.Max(0m, expectedFine - alreadyReceived);

                string paymentStatus;
                bool closeReturn = false;
                if (dto.TransferAmount == remaining)
                {
                    paymentStatus = "Đã thanh toán";
                    closeReturn = true;
                }
                else if (dto.TransferAmount < remaining)
                {
                    paymentStatus = "Đã thu một phần";
                    closeReturn = false;
                }
                else
                {
                    paymentStatus = "Thu dư";
                    closeReturn = true;
                }

                string displayContent = rawContent;
                if (displayContent.Length > 240) displayContent = displayContent.Substring(0, 240);

                var history = new tblPaymentHistory
                {
                    ReturnID = returnModel.ReturnID,
                    BorrowingID = returnModel.BorrowingID,
                    PatronID = returnModel.borrowing?.PatronID,
                    PatronName = returnModel.borrowing?.patron?.PatronName,
                    Amount = dto.TransferAmount,
                    PaymentMethod = "Chuyển khoản VietQR",
                    PaymentStatus = paymentStatus,
                    TransactionCode = txCode,
                    Provider = ProviderSePay,
                    ProviderTransactionId = providerTxId,
                    RawPayload = rawPayload,
                    TransactionContent = displayContent,
                    BankName = !string.IsNullOrWhiteSpace(dto.Gateway) ? dto.Gateway.Trim() : setting.BankName,
                    BankAccountNumber = !string.IsNullOrWhiteSpace(dto.AccountNumber) ? dto.AccountNumber.Trim() : setting.BankAccountNumber,
                    ConfirmedBy = "Hệ thống (SePay)",
                    PaymentDate = txDate
                };

                _context.PaymentHistories.Add(history);

                if (closeReturn)
                {
                    returnModel.IsActive = true;
                    _context.Returns.Update(returnModel);

                    if (returnModel.borrowing != null)
                    {
                        returnModel.borrowing.IsActive = false;
                        _context.Borrowings.Update(returnModel.borrowing);
                    }
                }

                await _context.SaveChangesAsync(ct);
                await dbTx.CommitAsync(ct);

                _logger.LogInformation(
                    "SePay reconciled ReturnID={ReturnId}, TxId={TxId}, Amount={Amount}, Status={Status}",
                    returnId, providerTxId, dto.TransferAmount, paymentStatus);

                return new ReconcileResult
                {
                    Payment = history,
                    Message = $"Đã ghi nhận giao dịch {txCode} ({paymentStatus})."
                };
            }
            catch (Exception ex)
            {
                await dbTx.RollbackAsync(ct);
                _logger.LogError(ex, "Error reconciling SePay transaction Id={TxId}", providerTxId);
                throw;
            }
        }

        public async Task<int> SyncFromSePayApiAsync(CancellationToken ct = default)
        {
            var apiToken = _options.Value.SePayApiToken;
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                return 0;
            }

            await EnsureTablesAsync(ct);

            var setting = await _feePaymentService.GetActiveSettingAsync();
            var account = !string.IsNullOrWhiteSpace(_options.Value.AccountNumber)
                ? _options.Value.AccountNumber.Trim()
                : (setting.BankAccountNumber ?? string.Empty).Trim();

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());

            var url = string.IsNullOrWhiteSpace(account)
                ? "https://my.sepay.vn/userapi/transactions/list?limit=20"
                : $"https://my.sepay.vn/userapi/transactions/list?account_number={Uri.EscapeDataString(account)}&limit=20";

            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("SePay API returned HTTP {StatusCode}", (int)resp.StatusCode);
                return 0;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("transactions", out var txArray) ||
                txArray.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            int processed = 0;
            foreach (var tx in txArray.EnumerateArray())
            {
                decimal amountIn = ParseDecimalProperty(tx, "amount_in");
                if (amountIn <= 0) continue;

                long id = 0;
                if (tx.TryGetProperty("id", out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number) idEl.TryGetInt64(out id);
                    else if (idEl.ValueKind == JsonValueKind.String) long.TryParse(idEl.GetString(), out id);
                }

                var dto = new SePayWebhookDto
                {
                    Id = id,
                    Gateway = GetStringProperty(tx, "bank_brand_name") ?? setting.BankName,
                    TransactionDate = GetStringProperty(tx, "transaction_date"),
                    AccountNumber = GetStringProperty(tx, "account_number") ?? account,
                    Content = GetStringProperty(tx, "transaction_content"),
                    TransferType = "in",
                    TransferAmount = amountIn,
                    ReferenceCode = GetStringProperty(tx, "reference_number")
                };

                var result = await HandleIncomingAsync(dto, tx.GetRawText(), ct);
                if (!result.IsDuplicate)
                {
                    processed++;
                }
            }

            return processed;
        }

        private async Task<tblUnmatchedBankTransaction> SaveUnmatchedAsync(
            SePayWebhookDto dto,
            string providerTxId,
            string referenceCode,
            DateTime txDate,
            string? rawPayload,
            string reason,
            CancellationToken ct)
        {
            var unmatched = new tblUnmatchedBankTransaction
            {
                Provider = ProviderSePay,
                ProviderTransactionId = providerTxId,
                ReferenceCode = referenceCode,
                Gateway = dto.Gateway,
                AccountNumber = dto.AccountNumber,
                TransferAmount = dto.TransferAmount,
                Content = dto.Content ?? dto.Description,
                Reason = reason,
                RawPayload = rawPayload,
                TransactionDate = txDate,
                CreatedAt = DateTime.Now,
                IsResolved = false
            };

            _context.UnmatchedBankTransactions.Add(unmatched);
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Saved unmatched SePay transaction Id={TxId}: {Reason}", providerTxId, reason);
            return unmatched;
        }

        public static List<int> ExtractReturnIds(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new List<int>();
            }

            // Chuẩn hóa: bỏ dấu tiếng Việt, viết hoa, thay ký tự đặc biệt thành khoảng trắng
            string normalized = RemoveDiacritics(content).ToUpperInvariant();
            string cleaned = Regex.Replace(normalized, @"[^A-Z0-9]+", " ");

            var ids = new HashSet<int>();

            // 1. Ưu tiên khớp theo ranh giới từ (\bRT62\b) - chống RT6 khớp nhầm RT62
            foreach (Match m in Regex.Matches(cleaned, @"\bRT(\d+)\b"))
            {
                if (int.TryParse(m.Groups[1].Value, out int id) && id > 0)
                {
                    ids.Add(id);
                }
            }

            // 2. Dự phòng nếu ngân hàng bỏ khoảng trắng (vd: THANHTOANPHATRT62) vẫn chống trùng tiền tố nhờ (?!\d)
            if (ids.Count == 0)
            {
                foreach (Match m in Regex.Matches(cleaned, @"RT(\d+)(?!\d)"))
                {
                    if (int.TryParse(m.Groups[1].Value, out int id) && id > 0)
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids.ToList();
        }

        private static string RemoveDiacritics(string input)
        {
            var formD = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (var ch in formD)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(ch == 'đ' ? 'd' : ch == 'Đ' ? 'D' : ch);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static DateTime ParseVietnamTransactionDate(string? dateStr)
        {
            if (!string.IsNullOrWhiteSpace(dateStr) &&
                DateTime.TryParseExact(
                    dateStr.Trim(),
                    new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "dd/MM/yyyy HH:mm:ss" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                return parsed;
            }

            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
            catch
            {
                return DateTime.Now;
            }
        }

        private static string? GetStringProperty(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
            return null;
        }

        private static decimal ParseDecimalProperty(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var prop)) return 0m;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var n)) return n;
            if (prop.ValueKind == JsonValueKind.String &&
                decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0m;
        }
    }
}
