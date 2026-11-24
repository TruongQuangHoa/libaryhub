using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibraryHub.Models;
using LibraryHub.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LibraryHub.Controllers
{
    [ApiController]
    [Route("api/payments")]
    public class SePayWebhookController : ControllerBase
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IOptions<VietQrPaymentOptions> _opt;
        private readonly IPaymentReconciler _reconciler;
        private readonly DataContext _context;
        private readonly ILogger<SePayWebhookController> _logger;

        public SePayWebhookController(
            IOptions<VietQrPaymentOptions> opt,
            IPaymentReconciler reconciler,
            DataContext context,
            ILogger<SePayWebhookController> logger)
        {
            _opt = opt;
            _reconciler = reconciler;
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Webhook nhận biến động số dư từ SePay (HTTP POST).
        /// URL: POST /api/payments/sepay-webhook
        /// Header bắt buộc: Authorization: Apikey &lt;WebhookApiKey&gt;
        /// </summary>
        [HttpPost("sepay-webhook")]
        [HttpPost("/api/payment/vietqr-webhook")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Receive(CancellationToken ct)
        {
            // 1. Xác thực API Key theo thời gian hằng (constant-time)
            var configuredKey = _opt.Value.WebhookApiKey;
            if (string.IsNullOrEmpty(configuredKey))
            {
                _logger.LogWarning("Rejected SePay webhook: WebhookApiKey is not configured.");
                return Unauthorized(new { success = false, message = "WebhookApiKey chưa được cấu hình." });
            }

            var expected = $"Apikey {configuredKey}";
            var actual = Request.Headers.Authorization.ToString();
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);

            if (actualBytes.Length != expectedBytes.Length ||
                !CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
            {
                _logger.LogWarning("Rejected SePay webhook: invalid Authorization header.");
                return Unauthorized(new { success = false, message = "Unauthorized." });
            }

            // 2. Đọc payload thô để lưu RawPayload phục vụ truy vết
            string rawPayload;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            {
                rawPayload = await reader.ReadToEndAsync(ct);
            }

            if (string.IsNullOrWhiteSpace(rawPayload))
            {
                return BadRequest(new { success = false, message = "Empty payload." });
            }

            SePayWebhookDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<SePayWebhookDto>(rawPayload, JsonOpts);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid SePay webhook JSON payload.");
                return BadRequest(new { success = false, message = "Invalid JSON." });
            }

            if (dto == null)
            {
                return BadRequest(new { success = false, message = "Null payload." });
            }

            // 3. Chỉ xử lý giao dịch tiền vào (transferType == "in"); giao dịch "out" bỏ qua nhưng vẫn trả 200
            if (!string.Equals(dto.TransferType, "in", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Ignored non-incoming SePay transaction Id={TxId}, Type={Type}", dto.Id, dto.TransferType);
                return Ok(new { success = true });
            }

            // 4. Đối soát & ghi lịch sử (idempotent)
            await _reconciler.HandleIncomingAsync(dto, rawPayload, ct);

            return Ok(new { success = true });
        }

        /// <summary>
        /// API cho giao diện polling kiểm tra trạng thái thanh toán của phiếu trả.
        /// URL: GET /api/payments/{returnId}/status
        /// </summary>
        [HttpGet("{returnId:int}/status")]
        [Authorize(Roles = "Admin,Librarian")]
        public async Task<IActionResult> GetStatus(int returnId, CancellationToken ct)
        {
            await _reconciler.EnsureTablesAsync(ct);

            // Nếu có SePayApiToken -> đồng bộ thêm từ API SePay (hữu ích khi chạy local)
            if (!string.IsNullOrWhiteSpace(_opt.Value.SePayApiToken))
            {
                await _reconciler.SyncFromSePayApiAsync(ct);
            }

            var latest = await _context.PaymentHistories
                .AsNoTracking()
                .Where(p => p.ReturnID == returnId)
                .OrderByDescending(p => p.PaymentDate)
                .FirstOrDefaultAsync(ct);

            if (latest != null && (latest.PaymentStatus == "Đã thanh toán" || latest.PaymentStatus == "Thu dư"))
            {
                return Ok(new
                {
                    status = "success",
                    paymentStatus = latest.PaymentStatus,
                    message = latest.PaymentStatus == "Thu dư"
                        ? $"Đã nhận thanh toán (Thu dư - Mã GD: {latest.TransactionCode})."
                        : $"Đã nhận thanh toán thành công (Mã GD: {latest.TransactionCode}).",
                    payment = new
                    {
                        paymentId = latest.PaymentID,
                        transactionCode = latest.TransactionCode ?? $"#{latest.PaymentID}",
                        paymentDate = latest.PaymentDate.ToString("dd/MM/yyyy HH:mm:ss"),
                        paymentMethod = latest.PaymentMethod,
                        amount = latest.Amount,
                        bankInfo = !string.IsNullOrWhiteSpace(latest.BankName)
                            ? $"{latest.BankName} - {latest.BankAccountNumber}"
                            : "-",
                        transactionContent = latest.TransactionContent ?? "-",
                        confirmedBy = latest.ConfirmedBy ?? "-",
                        paymentStatus = latest.PaymentStatus
                    }
                });
            }

            if (latest != null && latest.PaymentStatus == "Đã thu một phần")
            {
                return Ok(new
                {
                    status = "partial",
                    paymentStatus = latest.PaymentStatus,
                    message = $"Đã thu một phần ({latest.Amount:N0} VNĐ - Mã GD: {latest.TransactionCode}). Đang chờ phần còn lại.",
                    payment = new
                    {
                        paymentId = latest.PaymentID,
                        transactionCode = latest.TransactionCode ?? $"#{latest.PaymentID}",
                        paymentDate = latest.PaymentDate.ToString("dd/MM/yyyy HH:mm:ss"),
                        paymentMethod = latest.PaymentMethod,
                        amount = latest.Amount,
                        bankInfo = !string.IsNullOrWhiteSpace(latest.BankName)
                            ? $"{latest.BankName} - {latest.BankAccountNumber}"
                            : "-",
                        transactionContent = latest.TransactionContent ?? "-",
                        confirmedBy = latest.ConfirmedBy ?? "-",
                        paymentStatus = latest.PaymentStatus
                    }
                });
            }

            return Ok(new
            {
                status = "pending",
                notConfigured = !_reconciler.IsSePayConfigured(),
                message = _reconciler.IsSePayConfigured()
                    ? "Đang chờ biến động số dư từ SePay..."
                    : "Chưa cấu hình SePay (VietQrPayment:WebhookApiKey / SePayApiToken). Bạn có thể cấu hình SePay để tự động ghi nhận hoặc dùng đối soát thủ công bên dưới."
            });
        }
    }
}
