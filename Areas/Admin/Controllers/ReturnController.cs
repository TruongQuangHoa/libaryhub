using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using LibraryHub.Services;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Kernel.Pdf;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using iText.Kernel.Font;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class ReturnController : Controller
    {
        private readonly DataContext _context;
        private readonly ReturnService _returnService;
        private readonly IFeePaymentService _feePaymentService;
        private readonly IPaymentReconciler _paymentReconciler;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;

        public ReturnController(
            DataContext context,
            ReturnService returnService,
            IFeePaymentService feePaymentService,
            IPaymentReconciler paymentReconciler,
            Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _context = context;
            _returnService = returnService;
            _feePaymentService = feePaymentService;
            _paymentReconciler = paymentReconciler;
            _configuration = configuration;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? search)
        {
            var query = _context.Returns
                .Include(r => r.borrowing)
                    .ThenInclude(b => b!.patron)
                .Include(r => r.librarian)
                .AsQueryable();

            // Tìm kiếm theo tên hoặc email độc giả (tránh khớp nhầm tên miền như @gmail.com)
            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim();
                bool isEmailSearch = search.Contains('@');
                string emailPattern = isEmailSearch ? $"%{search}%" : $"%{search}%@%";

                var patronIDs = await _context.Patrons
                    .Where(p =>
                        EF.Functions.Like(p.PatronName, $"%{search}%") ||
                        (p.Email != null &&
                         EF.Functions.Like(p.Email, emailPattern))
                    )
                    .Select(p => p.PatronID)
                    .ToListAsync();

                query = query.Where(r =>
                    r.borrowing != null &&
                    patronIDs.Contains(r.borrowing.PatronID));
            }

            var returnList = await query
                .OrderByDescending(r => r.ReturnDate)
                .ToListAsync();

            await EnsurePaymentHistoryTableAsync();
            var paidReturnIds = (await _context.PaymentHistories
                .AsNoTracking()
                .Where(p => p.PaymentStatus == "Đã thanh toán" || p.PaymentStatus == "Thu dư")
                .Select(p => p.ReturnID)
                .Distinct()
                .ToListAsync())
                .ToHashSet();

            ViewBag.PaidReturnIds = paidReturnIds;
            ViewBag.Search = search;

            return View(returnList);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null || id == 0)
            {
                return NotFound();
            }

            var returnModel = await _context.Returns
                .Include(r => r.borrowing)
                    .ThenInclude(b => b!.patron)
                .Include(r => r.librarian)
                .Include(r => r.returnDetail)
                    .ThenInclude(d => d.book)
                .FirstOrDefaultAsync(r => r.ReturnID == id);

            if (returnModel == null)
            {
                return NotFound();
            }

            await EnsurePaymentHistoryTableAsync();
            var paymentHistories = await _context.PaymentHistories
                .AsNoTracking()
                .Where(p => p.ReturnID == returnModel.ReturnID)
                .OrderByDescending(p => p.PaymentDate)
                .ToListAsync();

            ViewBag.PaymentHistories = paymentHistories;

            return View(returnModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmPayment(int returnId, string paymentMethod)
        {
            try
            {
                var returnModel = await _context.Returns
                    .Include(r => r.borrowing)
                        .ThenInclude(b => b!.patron)
                    .FirstOrDefaultAsync(r => r.ReturnID == returnId);

                if (returnModel == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy phiếu trả." });
                }

                await EnsurePaymentHistoryTableAsync();
                bool alreadyPaid = await _context.PaymentHistories
                    .AnyAsync(p => p.ReturnID == returnModel.ReturnID && p.PaymentStatus == "Đã thanh toán");

                if (alreadyPaid)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Phiếu trả này đã thanh toán thành công, không thể thực hiện thanh toán nữa."
                    });
                }

                bool isCash = string.Equals(paymentMethod, "Cash", StringComparison.OrdinalIgnoreCase);
                bool isBank = string.Equals(paymentMethod, "Bank", StringComparison.OrdinalIgnoreCase);

                if (!isCash && !isBank)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Phương thức thanh toán không hợp lệ."
                    });
                }

                var amount = returnModel.TotalFine < 0 ? 0 : returnModel.TotalFine;
                string? bankName = null;
                string? bankAccountNumber = null;
                string transactionContent;
                string transactionCode;
                string paymentMethodDisplay;
                string providerName;

                if (isBank)
                {
                    var setting = await _feePaymentService.GetActiveSettingAsync();
                    bankName = setting.BankName;
                    bankAccountNumber = setting.BankAccountNumber;
                    transactionContent = $"THANH TOAN PHAT RT{returnModel.ReturnID}";
                    transactionCode = $"VIETQR-{returnModel.ReturnID}-{DateTime.Now:yyyyMMddHHmmss}";
                    paymentMethodDisplay = "Chuyển khoản VietQR";
                    providerName = "VietQR";
                }
                else
                {
                    transactionContent = $"Thanh toán tiền mặt phiếu trả #{returnModel.ReturnID}";
                    transactionCode = $"CASH-{returnModel.ReturnID}-{DateTime.Now:yyyyMMddHHmmss}";
                    paymentMethodDisplay = "Tiền mặt";
                    providerName = "Cash";
                }

                var confirmedBy = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Quản trị viên";

                var history = new tblPaymentHistory
                {
                    ReturnID = returnModel.ReturnID,
                    BorrowingID = returnModel.BorrowingID,
                    PatronID = returnModel.borrowing?.PatronID,
                    PatronName = returnModel.borrowing?.patron?.PatronName,
                    Amount = amount,
                    PaymentMethod = paymentMethodDisplay,
                    PaymentStatus = "Đã thanh toán",
                    TransactionCode = transactionCode,
                    Provider = providerName,
                    ProviderTransactionId = transactionCode,
                    TransactionContent = transactionContent,
                    BankName = bankName,
                    BankAccountNumber = bankAccountNumber,
                    ConfirmedBy = confirmedBy,
                    PaymentDate = DateTime.Now
                };

                _context.PaymentHistories.Add(history);

                // Cập nhật trạng thái phiếu trả thành "Đã hoàn tất" sau khi xác nhận thanh toán thành công
                returnModel.IsActive = true;
                _context.Returns.Update(returnModel);

                // Khi phiếu trả chuyển sang "Đã hoàn tất", chuyển trạng thái phiếu mượn tương ứng thành "Đã trả"
                if (returnModel.borrowing != null)
                {
                    returnModel.borrowing.IsActive = false;
                    _context.Borrowings.Update(returnModel.borrowing);
                }

                await _context.SaveChangesAsync();

                return Json(new
                {
                    success = true,
                    payment = MapPaymentDto(history)
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Không thể lưu lịch sử thanh toán: " + ex.Message });
            }
        }

        /// <summary>
        /// Đối soát gián tiếp giao dịch chuyển khoản ngân hàng:
        /// So sánh số tiền ngân hàng thực nhận với số tiền trên hệ thống, lưu lịch sử giao dịch (thành công hoặc thất bại) kèm mã giao dịch chính xác.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReconcileBankPayment(int returnId, string transactionCode, decimal receivedAmount)
        {
            try
            {
                await EnsurePaymentHistoryTableAsync();

                var returnModel = await _context.Returns
                    .Include(r => r.borrowing)
                        .ThenInclude(b => b!.patron)
                    .FirstOrDefaultAsync(r => r.ReturnID == returnId);

                if (returnModel == null)
                {
                    return Json(new { status = "error", message = "Không tìm thấy phiếu trả." });
                }

                bool alreadyPaid = await _context.PaymentHistories
                    .AnyAsync(p => p.ReturnID == returnModel.ReturnID && p.PaymentStatus == "Đã thanh toán");

                if (alreadyPaid)
                {
                    return Json(new
                    {
                        status = "error",
                        message = "Phiếu trả này đã thanh toán thành công và lưu lịch sử giao dịch trước đó."
                    });
                }

                transactionCode = (transactionCode ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(transactionCode))
                {
                    return Json(new { status = "error", message = "Vui lòng nhập mã giao dịch ngân hàng." });
                }

                if (receivedAmount < 0)
                {
                    return Json(new { status = "error", message = "Số tiền ngân hàng thực nhận không hợp lệ." });
                }

                bool duplicateTxCode = await _context.PaymentHistories
                    .AnyAsync(p => p.TransactionCode == transactionCode);

                if (duplicateTxCode)
                {
                    return Json(new
                    {
                        status = "error",
                        message = $"Mã giao dịch \"{transactionCode}\" đã tồn tại trong lịch sử giao dịch. Vui lòng kiểm tra lại."
                    });
                }

                var setting = await _feePaymentService.GetActiveSettingAsync();
                decimal expectedAmount = returnModel.TotalFine < 0 ? 0 : returnModel.TotalFine;
                bool isMatched = receivedAmount == expectedAmount;
                decimal diff = receivedAmount - expectedAmount;

                string baseContent = $"THANH TOAN PHAT RT{returnModel.ReturnID}";
                string detailContent = isMatched
                    ? $"{baseContent} (Đối soát khớp: {receivedAmount:N0} VNĐ)"
                    : $"{baseContent} (Không khớp: Ngân hàng nhận {receivedAmount:N0} VNĐ / Hệ thống yêu cầu {expectedAmount:N0} VNĐ, chênh lệch {(diff > 0 ? "+" : "")}{diff:N0} VNĐ)";

                var confirmedBy = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Quản trị viên";

                var history = new tblPaymentHistory
                {
                    ReturnID = returnModel.ReturnID,
                    BorrowingID = returnModel.BorrowingID,
                    PatronID = returnModel.borrowing?.PatronID,
                    PatronName = returnModel.borrowing?.patron?.PatronName,
                    Amount = receivedAmount,
                    PaymentMethod = "Chuyển khoản VietQR",
                    PaymentStatus = isMatched ? "Đã thanh toán" : "Thất bại",
                    TransactionCode = transactionCode,
                    Provider = "Manual",
                    ProviderTransactionId = transactionCode,
                    TransactionContent = detailContent,
                    BankName = setting.BankName,
                    BankAccountNumber = setting.BankAccountNumber,
                    ConfirmedBy = confirmedBy,
                    PaymentDate = DateTime.Now
                };

                _context.PaymentHistories.Add(history);

                if (isMatched)
                {
                    returnModel.IsActive = true;
                    _context.Returns.Update(returnModel);

                    if (returnModel.borrowing != null)
                    {
                        returnModel.borrowing.IsActive = false;
                        _context.Borrowings.Update(returnModel.borrowing);
                    }
                }

                await _context.SaveChangesAsync();

                return Json(new
                {
                    status = isMatched ? "success" : "failed",
                    message = isMatched
                        ? $"Đối soát thành công! Số tiền trùng khớp ({receivedAmount:N0} VNĐ) - Mã giao dịch: {transactionCode}."
                        : $"Đối soát thất bại! Số tiền không trùng khớp: Ngân hàng nhận {receivedAmount:N0} VNĐ nhưng hệ thống yêu cầu {expectedAmount:N0} VNĐ (Chênh lệch: {(diff > 0 ? "+" : "")}{diff:N0} VNĐ).",
                    payment = MapPaymentDto(history)
                });
            }
            catch (Exception ex)
            {
                return Json(new { status = "error", message = "Lỗi khi đối soát giao dịch: " + ex.Message });
            }
        }

        /// <summary>
        /// Hệ thống tự động kiểm tra trạng thái thanh toán VietQR (từ Webhook trong CSDL hoặc đồng bộ trực tiếp SePay API)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CheckPaymentStatus(int id)
        {
            try
            {
                await EnsurePaymentHistoryTableAsync();

                var returnModel = await _context.Returns
                    .Include(r => r.borrowing)
                        .ThenInclude(b => b!.patron)
                    .FirstOrDefaultAsync(r => r.ReturnID == id);

                if (returnModel == null)
                {
                    return Json(new { status = "error", message = "Không tìm thấy phiếu trả." });
                }

                // 1. Kiểm tra nếu đã có giao dịch thành công trong CSDL (do Webhook ghi nhận)
                var paidRecord = await _context.PaymentHistories
                    .AsNoTracking()
                    .Where(p => p.ReturnID == id && p.PaymentStatus == "Đã thanh toán")
                    .OrderByDescending(p => p.PaymentDate)
                    .FirstOrDefaultAsync();

                if (paidRecord != null)
                {
                    return Json(new
                    {
                        status = "success",
                        message = "Thanh toán qua ngân hàng thành công!",
                        payment = MapPaymentDto(paidRecord)
                    });
                }

                // 2. Nếu đã cấu hình SePayApiToken trong appsettings.json -> chủ động truy vấn giao dịch thật từ SePay API (hoạt động cả trên localhost)
                var sePayToken = _configuration["VietQrPayment:SePayApiToken"];
                bool hasSePayToken = !string.IsNullOrWhiteSpace(sePayToken);
                if (hasSePayToken)
                {
                    var setting = await _feePaymentService.GetActiveSettingAsync();
                    var syncResult = await SyncFromSePayApiAsync(returnModel, setting, sePayToken!.Trim());
                    if (syncResult != null)
                    {
                        return Json(syncResult);
                    }
                }

                // 3. Kiểm tra nếu vừa có giao dịch thất bại (vd: chuyển thiếu tiền) từ Webhook
                var failedRecord = await _context.PaymentHistories
                    .AsNoTracking()
                    .Where(p => p.ReturnID == id && p.PaymentStatus == "Thất bại")
                    .OrderByDescending(p => p.PaymentDate)
                    .FirstOrDefaultAsync();

                if (failedRecord != null)
                {
                    return Json(new
                    {
                        status = "failed",
                        message = failedRecord.TransactionContent ?? "Giao dịch chuyển khoản thất bại (số tiền không hợp lệ).",
                        payment = MapPaymentDto(failedRecord)
                    });
                }

                return Json(new
                {
                    status = "pending",
                    notConfigured = !hasSePayToken,
                    message = hasSePayToken
                        ? "Đang chờ giao dịch chuyển khoản từ ngân hàng..."
                        : "Chưa cấu hình VietQrPayment:SePayApiToken trong appsettings.json nên hệ thống chưa thể tự đọc biến động số dư từ ngân hàng."
                });
            }
            catch (Exception ex)
            {
                return Json(new { status = "error", message = "Lỗi kiểm tra giao dịch: " + ex.Message });
            }
        }

        private async Task<object?> SyncFromSePayApiAsync(tblReturn returnModel, tblPaymentSetting setting, string apiToken)
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);

            var account = Uri.EscapeDataString(setting.BankAccountNumber ?? string.Empty);
            var url = $"https://my.sepay.vn/userapi/transactions/list?account_number={account}&limit=20";

            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("transactions", out var txArray) ||
                txArray.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            // Hỗ trợ cả "THANH TOAN PHAT RT62" lẫn trường hợp ngân hàng bỏ khoảng trắng ("THANHTOANPHATRT62")
            var pattern = $@"RT{returnModel.ReturnID}(?!\d)";
            decimal expectedAmount = returnModel.TotalFine < 0 ? 0 : returnModel.TotalFine;

            foreach (var tx in txArray.EnumerateArray())
            {
                string content = tx.TryGetProperty("transaction_content", out var cProp)
                    ? (cProp.GetString() ?? string.Empty)
                    : string.Empty;

                if (!System.Text.RegularExpressions.Regex.IsMatch(content, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    continue;
                }

                decimal amountIn = 0m;
                if (tx.TryGetProperty("amount_in", out var aProp))
                {
                    if (aProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                        aProp.TryGetDecimal(out amountIn);
                    else if (aProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        decimal.TryParse(aProp.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out amountIn);
                }

                if (amountIn <= 0) continue;

                string txCode = string.Empty;
                if (tx.TryGetProperty("reference_number", out var rProp) && !string.IsNullOrWhiteSpace(rProp.GetString()))
                    txCode = rProp.GetString()!.Trim();
                else if (tx.TryGetProperty("id", out var idProp))
                    txCode = idProp.ToString();

                if (string.IsNullOrWhiteSpace(txCode))
                    txCode = $"SEPAY-{returnModel.ReturnID}-{DateTime.Now:yyyyMMddHHmmss}";

                // Bỏ qua nếu mã giao dịch này đã lưu trước đó
                bool exists = await _context.PaymentHistories.AnyAsync(p => p.TransactionCode == txCode);
                if (exists) continue;

                bool isSuccess = amountIn >= expectedAmount;
                var history = new tblPaymentHistory
                {
                    ReturnID = returnModel.ReturnID,
                    BorrowingID = returnModel.BorrowingID,
                    PatronID = returnModel.borrowing?.PatronID,
                    PatronName = returnModel.borrowing?.patron?.PatronName,
                    Amount = amountIn,
                    PaymentMethod = "Chuyển khoản ngân hàng (VietQR)",
                    PaymentStatus = isSuccess ? "Đã thanh toán" : "Thất bại",
                    TransactionCode = txCode,
                    TransactionContent = isSuccess
                        ? content
                        : $"{content} (Thiếu tiền: nhận {amountIn:N0} VNĐ / cần {expectedAmount:N0} VNĐ)",
                    BankName = setting.BankName,
                    BankAccountNumber = setting.BankAccountNumber,
                    ConfirmedBy = "Hệ thống (Tự động đối soát SePay)",
                    PaymentDate = DateTime.Now
                };

                _context.PaymentHistories.Add(history);

                if (isSuccess)
                {
                    returnModel.IsActive = true;
                    _context.Returns.Update(returnModel);
                    if (returnModel.borrowing != null)
                    {
                        returnModel.borrowing.IsActive = false;
                        _context.Borrowings.Update(returnModel.borrowing);
                    }
                }

                await _context.SaveChangesAsync();

                return new
                {
                    status = isSuccess ? "success" : "failed",
                    message = isSuccess
                        ? $"Thanh toán qua ngân hàng thành công! Mã giao dịch: {txCode}"
                        : $"Thanh toán thất bại: Nhận {amountIn:N0} VNĐ, thiếu so với {expectedAmount:N0} VNĐ (Mã GD: {txCode}).",
                    payment = MapPaymentDto(history)
                };
            }

            return null;
        }

        private static object MapPaymentDto(tblPaymentHistory history) => new
        {
            paymentId = history.PaymentID,
            transactionCode = history.TransactionCode ?? $"#{history.PaymentID}",
            paymentDate = history.PaymentDate.ToString("dd/MM/yyyy HH:mm:ss"),
            paymentMethod = history.PaymentMethod,
            amount = history.Amount,
            bankInfo = !string.IsNullOrWhiteSpace(history.BankName)
                ? $"{history.BankName} - {history.BankAccountNumber}"
                : "-",
            transactionContent = history.TransactionContent ?? "-",
            confirmedBy = history.ConfirmedBy ?? "-",
            paymentStatus = history.PaymentStatus
        };

        private Task EnsurePaymentHistoryTableAsync()
        {
            return _paymentReconciler.EnsureTablesAsync();
        }

        public IActionResult Create()
        {
            LoadData();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(tblReturn returnModel)
        {
            using (var transaction = await _context.Database.BeginTransactionAsync())
            {
                try
                {
                    if (!ModelState.IsValid)
                    {
                        LoadData();
                        return View(returnModel);
                    }

                    var borrowing = await _context.Borrowings
                        .FirstOrDefaultAsync(b => b.BorrowingID == returnModel.BorrowingID && b.IsActive);

                    if (borrowing == null)
                    {
                        ModelState.AddModelError("BorrowingID", "Phiếu mượn không tồn tại hoặc đã hoàn tất.");
                        LoadData();
                        return View(returnModel);
                    }

                    if (returnModel.ReturnDate.HasValue && borrowing.BorrowDate.HasValue &&
                        returnModel.ReturnDate.Value < borrowing.BorrowDate.Value)
                    {
                        ModelState.AddModelError("ReturnDate", "Ngày trả không thể nhỏ hơn ngày mượn.");
                        LoadData();
                        return View(returnModel);
                    }

                    // Chỉ tạo phiếu trả, chưa có chi tiết
                    returnModel.TotalFine = 0;
                    returnModel.IsActive = false;

                    _context.Returns.Add(returnModel);
                    await _context.SaveChangesAsync();

                    await transaction.CommitAsync();

                    TempData["Success"] = "Tạo phiếu trả thành công! Vui lòng nhập chi tiết sách trả.";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    ModelState.AddModelError("", "Lỗi khi tạo phiếu trả.");
                    LoadData();
                    return View(returnModel);
                }
            }
        }

        // Các hàm Edit sẽ được thêm vào đây, bên dưới hàm Create(tblReturn)

        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            // Nạp phiếu trả, bao gồm chi tiết trả và phiếu mượn gốc
            // Nạp phiếu trả, bao gồm chi tiết trả và phiếu mượn gốc, và ĐỘC GIẢ
            var returnModel = await _context.Returns
                .Include(r => r.returnDetail)
                    .ThenInclude(d => d.book)
                .Include(r => r.borrowing)
                    .ThenInclude(b => b.patron) // <<< THÊM DÒNG NÀY ĐỂ INCLUDE ĐỘC GIẢ
                .FirstOrDefaultAsync(m => m.ReturnID == id);

            if (returnModel == null)
            {
                return NotFound();
            }

            // Load dữ liệu cho DropdownList (cũng cần thiết cho trường hợp Model.IsValid = false)
            LoadData();

            // Đảm bảo chỉ có phiếu mượn gốc được chọn trong dropdownlist
            // (Mặc dù việc chỉnh sửa phiếu trả không nên cho phép đổi phiếu mượn gốc)
            var selectedBorrowing = new SelectListItem
            {
                Value = returnModel.BorrowingID.ToString(),
                Text = $"{returnModel.BorrowingID} - Phiếu mượn gốc",
                Selected = true
            };

            // Thay thế danh sách Phiếu mượn để chỉ bao gồm phiếu đã chọn
            ViewBag.borrowingList = new SelectList(new[] { selectedBorrowing }, "Value", "Text", selectedBorrowing.Value);

            return View(returnModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, tblReturn returnModel)
        {
            if (id != returnModel.ReturnID)
            {
                return NotFound();
            }

            using (var transaction = _context.Database.BeginTransaction())
            {
                try
                {
                    var existingReturn = await _context.Returns
                        .Include(r => r.returnDetail!)
                            .ThenInclude(d => d.book)
                        .Include(r => r.borrowing!)
                            .ThenInclude(b => b.patron)
                        .Include(r => r.librarian)
                        .FirstOrDefaultAsync(r => r.ReturnID == id);

                    if (existingReturn == null)
                    {
                        return NotFound();
                    }

                    var borrowing = existingReturn.borrowing;

                    // Kiểm tra ngày trả < ngày mượn (so sánh đầy đủ cả giờ, phút, giây giống Create)
                    if (returnModel.ReturnDate.HasValue && borrowing.BorrowDate.HasValue)
                    {
                        if (returnModel.ReturnDate.Value < borrowing.BorrowDate.Value)
                        {
                            ModelState.AddModelError("ReturnDate",
                                $"Ngày trả sách không thể nhỏ hơn ngày mượn ({borrowing.BorrowDate.Value:dd/MM/yyyy HH:mm}).");
                        }
                    }

                    if (!ModelState.IsValid)
                    {
                        // phần lỗi giữ nguyên như cũ của bạn
                        existingReturn.ReturnDate = returnModel.ReturnDate;
                        existingReturn.LibrarianID = returnModel.LibrarianID;

                        var laList = await _context.Librarians.Where(l => l.IsActive)
                            .Select(l => new { l.LibrarianID, Info = l.LibrarianID + " - " + l.FullName })
                            .ToListAsync();
                        ViewBag.laList = new SelectList(laList, "LibrarianID", "Info", returnModel.LibrarianID);

                        return View(existingReturn);
                    }

                    // === DÁN NGUYÊN ĐOẠN NÀY VÀO ĐÂY – XÓA HẾT PHẦN CŨ TỪ DÒNG “XỬ LÝ LƯU THÀNH CÔNG” TRỞ XUỐNG ===
                    DateTime newReturnDate = returnModel.ReturnDate.Value;

                    // Cập nhật ngày trả và thủ thư trước
                    existingReturn.ReturnDate = newReturnDate;
                    existingReturn.LibrarianID = returnModel.LibrarianID;

                    // Cập nhật phí quá hạn cho tất cả chi tiết (vì ngày trả thay đổi) theo mức phạt trong CSDL
                    var overdueDays = await _feePaymentService.CalculateOverdueDaysAsync(borrowing.DueDate, newReturnDate);
                    var overdueFine = await _feePaymentService.CalculateOverdueFineAsync(borrowing.DueDate, newReturnDate);
                    foreach (var detail in existingReturn.returnDetail!)
                    {
                        detail.OverdueDays = overdueDays;
                        detail.OverdueFine = overdueFine;
                    }

                    _context.ReturnDetails.UpdateRange(existingReturn.returnDetail!);
                    _context.Returns.Update(existingReturn);

                    await _context.SaveChangesAsync();

                    // === QUAN TRỌNG: GỌI HÀM CHUNG ĐỂ TÍNH LẠI TỔNG PHẠT (BAO GỒM CẢ LOSTFINE) ===
                    await _returnService.UpdateTotalFineAndStatus(id);

                    await transaction.CommitAsync();

                    TempData["Success"] = $"Chỉnh sửa phiếu trả ID {id} thành công!";
                    return RedirectToAction(nameof(Index));
                    // === HẾT ĐOẠN DÁN ===
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    // phần catch giữ nguyên như cũ của bạn
                    var existingReturn = await _context.Returns
                        .Include(r => r.returnDetail!).ThenInclude(d => d.book)
                        .Include(r => r.borrowing!).ThenInclude(b => b.patron)
                        .FirstOrDefaultAsync(r => r.ReturnID == id);

                    if (existingReturn != null)
                    {
                        existingReturn.ReturnDate = returnModel.ReturnDate;
                        existingReturn.LibrarianID = returnModel.LibrarianID;

                        var laList = await _context.Librarians.Where(l => l.IsActive)
                            .Select(l => new { l.LibrarianID, Info = l.LibrarianID + " - " + l.FullName })
                            .ToListAsync();
                        ViewBag.laList = new SelectList(laList, "LibrarianID", "Info", returnModel.LibrarianID);

                        ModelState.AddModelError("", "Có lỗi xảy ra khi lưu dữ liệu.");
                        return View(existingReturn);
                    }

                    return View(returnModel);
                }
            }
        }

        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            // Nạp phiếu trả cùng với chi tiết và phiếu mượn gốc (để hiển thị thông tin xác nhận)
            var returnModel = await _context.Returns
                .Include(r => r.returnDetail)
                    .ThenInclude(d => d.book) // Nạp thông tin sách
                .Include(r => r.borrowing)
                    .ThenInclude(b => b.patron) // Nạp thông tin độc giả
                .Include(r => r.librarian) // Nạp thông tin thủ thư
                .FirstOrDefaultAsync(m => m.ReturnID == id);

            if (returnModel == null)
            {
                return NotFound();
            }

            return View(returnModel);
        }

        // POST: Admin/Return/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            // Khởi tạo Transaction
            using (var transaction = _context.Database.BeginTransaction())
            {
                try
                {
                    var returnToDelete = await _context.Returns
                        .Include(r => r.returnDetail)
                        .Include(r => r.borrowing)
                        .FirstOrDefaultAsync(r => r.ReturnID == id);

                    if (returnToDelete == null)
                    {
                        return NotFound();
                    }

                    var borrowing = returnToDelete.borrowing;

                    // 1. Hoàn nguyên Sách và Kích hoạt lại Chi tiết mượn
                    if (returnToDelete.returnDetail != null && returnToDelete.returnDetail.Any())
                    {
                        // Lấy danh sách BookID đã được trả trong phiếu này
                        var bookIDs = returnToDelete.returnDetail.Select(d => d.BookID).ToList();
                        var booksToUpdate = await _context.Books
                            .Where(b => bookIDs.Contains(b.BookID))
                            .ToListAsync();

                        // Lấy chi tiết mượn tương ứng để kích hoạt lại
                        var borrowingDetailsToReactivate = await _context.BorrowingDetails
                            .Where(d => d.BorrowingID == returnToDelete.BorrowingID && bookIDs.Contains(d.BookID))
                            .ToListAsync();

                        foreach (var detail in borrowingDetailsToReactivate)
                        {
                            // Kích hoạt lại chi tiết mượn
                            detail.IsActive = true;
                        }
                        _context.BorrowingDetails.UpdateRange(borrowingDetailsToReactivate);


                        // Hoàn nguyên BorrowQuantity cho mỗi sách
                        foreach (var book in booksToUpdate)
                        {
                            // Giả định mỗi chi tiết trả chỉ tương ứng với 1 lần mượn (số lượng 1)
                            book.BorrowQuantity += 1;
                        }
                        _context.Books.UpdateRange(booksToUpdate);
                    }

                    // 2. Kích hoạt lại Phiếu mượn gốc (Nếu nó bị vô hiệu hóa)
                    if (borrowing != null && borrowing.IsActive == false)
                    {
                        // Kích hoạt lại phiếu mượn nếu nó đã bị đánh dấu là hoàn tất
                        borrowing.IsActive = true;
                        _context.Borrowings.Update(borrowing);
                    }

                    // 3. Xóa Chi tiết Trả (Cascade Delete thường được Entity Framework xử lý,
                    // nhưng làm rõ ràng để đảm bảo)
                    if (returnToDelete.returnDetail != null)
                    {
                        _context.ReturnDetails.RemoveRange(returnToDelete.returnDetail);
                    }

                    // 4. Xóa Phiếu Trả
                    _context.Returns.Remove(returnToDelete);

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    TempData["Success"] = $"Xóa phiếu trả ID {id} và hoàn nguyên sách thành công!";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    // Ghi log lỗi (nên dùng ILogger)
                    ModelState.AddModelError("", $"Đã xảy ra lỗi hệ thống khi xóa phiếu trả: {ex.Message}");

                    // Tải lại dữ liệu cho View nếu có lỗi (để hiển thị lỗi)
                    var returnModel = await _context.Returns
                        .Include(r => r.returnDetail)
                            .ThenInclude(d => d.book)
                        .Include(r => r.borrowing)
                            .ThenInclude(b => b.patron)
                        .Include(r => r.librarian)
                        .FirstOrDefaultAsync(m => m.ReturnID == id);

                    return View("Delete", returnModel);
                }
            }
        }

        // Hàm LoadData cho DropdownLists
        private void LoadData()
        {
            // Lấy danh sách các BorrowingID đã có trong bảng Phiếu trả (Returns) để loại trừ
            var existingReturnBorrowingIds = _context.Returns.Select(r => r.BorrowingID).ToList();

            // 1. Lấy danh sách Phiếu mượn đang Hoạt động (IsActive = true) và CHƯA có phiếu trả
            var activeBorrowings = _context.Borrowings
                .Where(b => b.IsActive == true && !existingReturnBorrowingIds.Contains(b.BorrowingID))
                .Include(b => b.patron) // Bao gồm thông tin độc giả để hiển thị dễ dàng
                .Select(b => new
                {
                    b.BorrowingID,
                    Info = b.BorrowingID + " - Độc giả: " + b.patron!.PatronName + " - Ngày mượn: " + b.BorrowDate!.Value.ToString("dd/MM/yyyy")
                }).ToList();

            ViewBag.borrowingList = new SelectList(activeBorrowings, "BorrowingID", "Info");

            // 2. Lấy dữ liệu thủ thư (những người có thể nhận sách trả)
            var laList = _context.Librarians.Where(l => l.IsActive == true)
            .Select(la => new
            {
                la.LibrarianID,
                Info = la.LibrarianID + " - " + la.FullName
            }).ToList();

            ViewBag.laList = new SelectList(laList, "LibrarianID", "Info");
        }

        // Action: Xuất phiếu trả sách (PDF hoặc Word)
        public async Task<IActionResult> Export(int id, string format)
        {
            // Lấy phiếu trả với đầy đủ thông tin liên quan
            var returnEntity = await _context.Returns
                .Include(r => r.borrowing!)
                    .ThenInclude(b => b!.patron)
                .Include(r => r.borrowing!)
                    .ThenInclude(b => b!.librarian)
                .Include(r => r.librarian)
                .Include(r => r.returnDetail!)
                    .ThenInclude(rd => rd.book)
                .FirstOrDefaultAsync(r => r.ReturnID == id);

            if (returnEntity == null)
            {
                return NotFound();
            }

            await EnsurePaymentHistoryTableAsync();
            var latestPayment = await _context.PaymentHistories
                .AsNoTracking()
                .Where(p => p.ReturnID == returnEntity.ReturnID && p.PaymentStatus == "Đã thanh toán")
                .OrderByDescending(p => p.PaymentDate)
                .FirstOrDefaultAsync();

            var details = returnEntity.returnDetail?.ToList() ?? new List<tblReturnDetail>();
            var now = DateTime.Now;
            string dateText = $"TP. Vinh, ngày {now.Day:D2} tháng {now.Month:D2} năm {now.Year}";
            string patronName = returnEntity.borrowing?.patron?.PatronName ?? "N/A";
            string patronEmail = string.IsNullOrWhiteSpace(returnEntity.borrowing?.patron?.Email) ? "-" : returnEntity.borrowing!.patron!.Email!;
            string patronPhone = string.IsNullOrWhiteSpace(returnEntity.borrowing?.patron?.Phone) ? "-" : returnEntity.borrowing!.patron!.Phone!;
            string librarianName = returnEntity.librarian?.FullName ?? "N/A";
            string borrowDateText = returnEntity.borrowing?.BorrowDate?.ToString("dd/MM/yyyy HH:mm") ?? "-";
            string dueDateText = returnEntity.borrowing?.DueDate?.ToString("dd/MM/yyyy HH:mm") ?? "-";
            string returnDateText = returnEntity.ReturnDate?.ToString("dd/MM/yyyy HH:mm") ?? "-";
            string statusText = latestPayment != null ? "Đã hoàn tất" : "Chưa hoàn tất";
            string paymentMethodText = latestPayment?.PaymentMethod ?? "Chưa thanh toán";
            string transactionCodeText = !string.IsNullOrWhiteSpace(latestPayment?.TransactionCode) ? latestPayment!.TransactionCode! : "-";
            string paymentDateText = latestPayment != null ? latestPayment.PaymentDate.ToString("dd/MM/yyyy HH:mm") : "-";
            string totalFineText = $"{returnEntity.TotalFine:N0} VNĐ";
            decimal totalOverdueFine = details.Sum(d => d.OverdueFine);
            decimal totalLostFine = details.Sum(d => d.LostFine);

            using (var memoryStream = new MemoryStream())
            {
                if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
                {
                    // Tạo PDF bằng iText7
                    PdfWriter writer = new PdfWriter(memoryStream);
                    PdfDocument pdf = new PdfDocument(writer);
                    pdf.SetDefaultPageSize(iText.Kernel.Geom.PageSize.A4);
                    iText.Layout.Document document = new iText.Layout.Document(pdf);
                    document.SetMargins(38, 40, 38, 40);

                    string fontPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "fonts", "arial.ttf");
                    string fontBoldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "fonts", "arialbd.ttf");
                    string fontItalicPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "fonts", "ariali.ttf");

                    if (!System.IO.File.Exists(fontPath) || !System.IO.File.Exists(fontBoldPath))
                    {
                        return BadRequest("Không tìm thấy file font Arial trong wwwroot/fonts/");
                    }

                    PdfFont normalFont = PdfFontFactory.CreateFont(fontPath, iText.IO.Font.PdfEncodings.IDENTITY_H);
                    PdfFont boldFont = PdfFontFactory.CreateFont(fontBoldPath, iText.IO.Font.PdfEncodings.IDENTITY_H);
                    PdfFont italicFont = System.IO.File.Exists(fontItalicPath)
                        ? PdfFontFactory.CreateFont(fontItalicPath, iText.IO.Font.PdfEncodings.IDENTITY_H)
                        : normalFont;

                    // Bảng màu đồng bộ với Phiếu mượn
                    var navyColor = new iText.Kernel.Colors.DeviceRgb(27, 54, 93);       // #1B365D
                    var textColor = new iText.Kernel.Colors.DeviceRgb(30, 41, 59);       // #1E293B
                    var mutedColor = new iText.Kernel.Colors.DeviceRgb(71, 85, 105);     // #475569
                    var lightBgColor = new iText.Kernel.Colors.DeviceRgb(248, 250, 252); // #F8FAFC
                    var totalBgColor = new iText.Kernel.Colors.DeviceRgb(238, 242, 246); // #EEF2F6
                    var borderColor = new iText.Kernel.Colors.DeviceRgb(203, 213, 225);  // #CBD5E1
                    var accentRed = new iText.Kernel.Colors.DeviceRgb(185, 28, 28);      // #B91C1C
                    var thinBorder = new iText.Layout.Borders.SolidBorder(borderColor, 0.75f);

                    // === 1. HEADER QUỐC HIỆU & TIÊU NGỮ (2 CỘT CÂN ĐỐI) ===
                    var headerTable = new iText.Layout.Element.Table(UnitValue.CreatePercentArray(new float[] { 43, 57 }))
                        .UseAllAvailableWidth()
                        .SetMarginBottom(16);

                    var leftHeaderCell = new Cell()
                        .SetBorder(iText.Layout.Borders.Border.NO_BORDER)
                        .SetPadding(0)
                        .Add(new iText.Layout.Element.Paragraph("BỘ GIÁO DỤC VÀ ĐÀO TẠO")
                            .SetFont(normalFont).SetFontSize(10.5f).SetFontColor(textColor)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                            .SetMargin(0))
                        .Add(new iText.Layout.Element.Paragraph("TRƯỜNG ĐẠI HỌC VINH")
                            .SetFont(boldFont).SetFontSize(11f).SetFontColor(navyColor)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                            .SetMarginTop(2).SetMarginBottom(0))
                        .Add(new iText.Layout.Element.Div()
                            .SetWidth(75)
                            .SetBorderBottom(new iText.Layout.Borders.SolidBorder(navyColor, 1f))
                            .SetHorizontalAlignment(iText.Layout.Properties.HorizontalAlignment.CENTER)
                            .SetMarginTop(4));

                    var rightHeaderCell = new Cell()
                        .SetBorder(iText.Layout.Borders.Border.NO_BORDER)
                        .SetPadding(0)
                        .Add(new iText.Layout.Element.Paragraph("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM")
                            .SetFont(boldFont).SetFontSize(10.5f).SetFontColor(textColor)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                            .SetMargin(0))
                        .Add(new iText.Layout.Element.Paragraph("Độc lập - Tự do - Hạnh phúc")
                            .SetFont(boldFont).SetFontSize(11f).SetFontColor(textColor)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                            .SetMarginTop(2).SetMarginBottom(0))
                        .Add(new iText.Layout.Element.Div()
                            .SetWidth(115)
                            .SetBorderBottom(new iText.Layout.Borders.SolidBorder(textColor, 1f))
                            .SetHorizontalAlignment(iText.Layout.Properties.HorizontalAlignment.CENTER)
                            .SetMarginTop(4));

                    headerTable.AddCell(leftHeaderCell);
                    headerTable.AddCell(rightHeaderCell);
                    document.Add(headerTable);

                    // === 2. TIÊU ĐỀ CHÍNH ===
                    document.Add(new iText.Layout.Element.Paragraph("PHIẾU TRẢ SÁCH")
                        .SetFont(boldFont)
                        .SetFontSize(17)
                        .SetFontColor(navyColor)
                        .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                        .SetMarginTop(6)
                        .SetMarginBottom(2));

                    document.Add(new iText.Layout.Element.Paragraph($"Mã phiếu trả: #{returnEntity.ReturnID}   |   Phiếu mượn gốc: #{returnEntity.BorrowingID}")
                        .SetFont(italicFont)
                        .SetFontSize(10.5f)
                        .SetFontColor(mutedColor)
                        .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                        .SetMarginTop(0)
                        .SetMarginBottom(16));

                    // === 3. KHUNG THÔNG TIN PHIẾU TRẢ (2 CỘT CÂN ĐỐI) ===
                    var infoTable = new iText.Layout.Element.Table(UnitValue.CreatePercentArray(new float[] { 50, 50 }))
                        .UseAllAvailableWidth()
                        .SetBackgroundColor(lightBgColor)
                        .SetBorder(thinBorder)
                        .SetMarginBottom(18);

                    Cell CreatePdfInfoCell(string label, string value, bool highlightValue = false, bool lastRow = false)
                    {
                        var p = new iText.Layout.Element.Paragraph()
                            .SetMargin(0)
                            .SetMultipliedLeading(1.2f)
                            .Add(new iText.Layout.Element.Text(label + ": ")
                                .SetFont(boldFont).SetFontSize(10f).SetFontColor(mutedColor))
                            .Add(new iText.Layout.Element.Text(value)
                                .SetFont(highlightValue ? boldFont : normalFont)
                                .SetFontSize(10f)
                                .SetFontColor(highlightValue ? accentRed : textColor));

                        var cell = new Cell()
                            .SetBorder(iText.Layout.Borders.Border.NO_BORDER)
                            .SetPaddingTop(6)
                            .SetPaddingBottom(6)
                            .SetPaddingLeft(10)
                            .SetPaddingRight(10)
                            .Add(p);

                        if (!lastRow)
                        {
                            cell.SetBorderBottom(new iText.Layout.Borders.SolidBorder(borderColor, 0.4f));
                        }

                        return cell;
                    }

                    infoTable.AddCell(CreatePdfInfoCell("Độc giả", patronName));
                    infoTable.AddCell(CreatePdfInfoCell("Trạng thái", statusText));
                    infoTable.AddCell(CreatePdfInfoCell("Email", patronEmail));
                    infoTable.AddCell(CreatePdfInfoCell("Số điện thoại", patronPhone));
                    infoTable.AddCell(CreatePdfInfoCell("Ngày mượn gốc", borrowDateText));
                    infoTable.AddCell(CreatePdfInfoCell("Hạn trả gốc", dueDateText));
                    infoTable.AddCell(CreatePdfInfoCell("Ngày trả thực tế", returnDateText));
                    infoTable.AddCell(CreatePdfInfoCell("Thủ thư nhận trả", librarianName));
                    infoTable.AddCell(CreatePdfInfoCell("Phương thức TT", paymentMethodText));
                    infoTable.AddCell(CreatePdfInfoCell("Mã giao dịch", transactionCodeText));
                    infoTable.AddCell(CreatePdfInfoCell("Tổng tiền phạt", totalFineText, highlightValue: true, lastRow: true));
                    infoTable.AddCell(CreatePdfInfoCell("Thời gian TT", paymentDateText, lastRow: true));

                    document.Add(infoTable);

                    // === 4. BẢNG CHI TIẾT SÁCH TRẢ ===
                    document.Add(new iText.Layout.Element.Paragraph("CHI TIẾT SÁCH TRẢ")
                        .SetFont(boldFont)
                        .SetFontSize(11.5f)
                        .SetFontColor(navyColor)
                        .SetMarginTop(0)
                        .SetMarginBottom(8));

                    var table = new iText.Layout.Element.Table(UnitValue.CreatePercentArray(new float[] { 6, 9, 31, 10, 15, 13, 16 }))
                        .UseAllAvailableWidth();

                    string[] headers = { "STT", "Mã sách", "Tên sách", "Quá hạn", "Phạt quá hạn", "Tình trạng", "Phạt mất/hỏng" };
                    foreach (var h in headers)
                    {
                        table.AddHeaderCell(new Cell()
                            .SetBackgroundColor(navyColor)
                            .SetBorder(thinBorder)
                            .SetPaddingTop(7)
                            .SetPaddingBottom(7)
                            .SetPaddingLeft(4)
                            .SetPaddingRight(4)
                            .SetVerticalAlignment(VerticalAlignment.MIDDLE)
                            .Add(new iText.Layout.Element.Paragraph(h)
                                .SetFont(boldFont)
                                .SetFontSize(9.5f)
                                .SetFontColor(iText.Kernel.Colors.ColorConstants.WHITE)
                                .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                                .SetMargin(0)));
                    }

                    if (details.Any())
                    {
                        int stt = 1;
                        foreach (var detail in details)
                        {
                            var rowBg = stt % 2 == 0 ? lightBgColor : iText.Kernel.Colors.ColorConstants.WHITE;

                            Cell CreatePdfDataCell(string text, iText.Layout.Properties.TextAlignment align)
                            {
                                return new Cell()
                                    .SetBackgroundColor(rowBg)
                                    .SetBorder(thinBorder)
                                    .SetPaddingTop(6)
                                    .SetPaddingBottom(6)
                                    .SetPaddingLeft(5)
                                    .SetPaddingRight(5)
                                    .SetVerticalAlignment(VerticalAlignment.MIDDLE)
                                    .Add(new iText.Layout.Element.Paragraph(text)
                                        .SetFont(normalFont)
                                        .SetFontSize(9.5f)
                                        .SetFontColor(textColor)
                                        .SetTextAlignment(align)
                                        .SetMargin(0));
                            }

                            table.AddCell(CreatePdfDataCell(stt.ToString(), iText.Layout.Properties.TextAlignment.CENTER));
                            table.AddCell(CreatePdfDataCell(detail.BookID.ToString(), iText.Layout.Properties.TextAlignment.CENTER));
                            table.AddCell(CreatePdfDataCell(detail.book?.TitleBook ?? "N/A", iText.Layout.Properties.TextAlignment.LEFT));
                            table.AddCell(CreatePdfDataCell($"{detail.OverdueDays} ngày", iText.Layout.Properties.TextAlignment.CENTER));
                            table.AddCell(CreatePdfDataCell($"{detail.OverdueFine:N0} VNĐ", iText.Layout.Properties.TextAlignment.RIGHT));
                            table.AddCell(CreatePdfDataCell(detail.IsDamaged ? "Hư hỏng/Mất" : "Bình thường", iText.Layout.Properties.TextAlignment.CENTER));
                            table.AddCell(CreatePdfDataCell($"{detail.LostFine:N0} VNĐ", iText.Layout.Properties.TextAlignment.RIGHT));
                            stt++;
                        }
                    }
                    else
                    {
                        table.AddCell(new Cell(1, 7)
                            .SetBorder(thinBorder)
                            .SetPadding(10)
                            .Add(new iText.Layout.Element.Paragraph("Chưa có sách nào trong phiếu trả này.")
                                .SetFont(italicFont)
                                .SetFontSize(10f)
                                .SetFontColor(mutedColor)
                                .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                                .SetMargin(0)));
                    }

                    // Hàng tổng cộng
                    table.AddCell(new Cell(1, 4)
                        .SetBackgroundColor(totalBgColor)
                        .SetBorder(thinBorder)
                        .SetPaddingTop(7).SetPaddingBottom(7).SetPaddingRight(6)
                        .Add(new iText.Layout.Element.Paragraph($"Tổng cộng ({details.Count} cuốn):")
                            .SetFont(boldFont).SetFontSize(9.5f).SetFontColor(navyColor)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.RIGHT)
                            .SetMargin(0)));

                    table.AddCell(new Cell()
                        .SetBackgroundColor(totalBgColor)
                        .SetBorder(thinBorder)
                        .SetPaddingTop(7).SetPaddingBottom(7).SetPaddingRight(5)
                        .Add(new iText.Layout.Element.Paragraph($"{totalOverdueFine:N0} VNĐ")
                            .SetFont(boldFont).SetFontSize(9.5f).SetFontColor(textColor)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.RIGHT)
                            .SetMargin(0)));

                    table.AddCell(new Cell()
                        .SetBackgroundColor(totalBgColor)
                        .SetBorder(thinBorder)
                        .SetPaddingTop(7).SetPaddingBottom(7)
                        .Add(new iText.Layout.Element.Paragraph("Tổng phạt:")
                            .SetFont(boldFont).SetFontSize(9.5f).SetFontColor(navyColor)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                            .SetMargin(0)));

                    table.AddCell(new Cell()
                        .SetBackgroundColor(totalBgColor)
                        .SetBorder(thinBorder)
                        .SetPaddingTop(7).SetPaddingBottom(7).SetPaddingRight(5)
                        .Add(new iText.Layout.Element.Paragraph(totalFineText)
                            .SetFont(boldFont).SetFontSize(9.5f).SetFontColor(accentRed)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.RIGHT)
                            .SetMargin(0)));

                    document.Add(table);

                    // === 5. PHẦN CHỮ KÝ (CÂN ĐỐI 2 BÊN) ===
                    document.Add(new iText.Layout.Element.Paragraph(dateText)
                        .SetFont(italicFont)
                        .SetFontSize(10.5f)
                        .SetFontColor(textColor)
                        .SetTextAlignment(iText.Layout.Properties.TextAlignment.RIGHT)
                        .SetMarginTop(22)
                        .SetMarginBottom(8)
                        .SetPaddingRight(20));

                    var signatureTable = new iText.Layout.Element.Table(UnitValue.CreatePercentArray(new float[] { 50, 50 }))
                        .UseAllAvailableWidth();

                    Cell CreatePdfSignatureCell(string roleTitle, string signerName)
                    {
                        return new Cell()
                            .SetBorder(iText.Layout.Borders.Border.NO_BORDER)
                            .SetPadding(0)
                            .Add(new iText.Layout.Element.Paragraph(roleTitle)
                                .SetFont(boldFont).SetFontSize(11f).SetFontColor(navyColor)
                                .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                                .SetMargin(0))
                            .Add(new iText.Layout.Element.Paragraph("(Ký, ghi rõ họ tên)")
                                .SetFont(italicFont).SetFontSize(9.5f).SetFontColor(mutedColor)
                                .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                                .SetMarginTop(2).SetMarginBottom(46))
                            .Add(new iText.Layout.Element.Paragraph(signerName)
                                .SetFont(boldFont).SetFontSize(10.5f).SetFontColor(textColor)
                                .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                                .SetMargin(0));
                    }

                    signatureTable.AddCell(CreatePdfSignatureCell("ĐỘC GIẢ", patronName));
                    signatureTable.AddCell(CreatePdfSignatureCell("THỦ THƯ NHẬN TRẢ", librarianName));

                    document.Add(signatureTable);
                    document.Close();

                    return File(memoryStream.ToArray(), "application/pdf", $"PhieuTra_{id}.pdf");
                }
                else if (string.Equals(format, "word", StringComparison.OrdinalIgnoreCase))
                {
                    using (WordprocessingDocument wordDoc = WordprocessingDocument.Create(memoryStream, WordprocessingDocumentType.Document))
                    {
                        MainDocumentPart mainPart = wordDoc.AddMainDocumentPart();
                        mainPart.Document = new Document();
                        Body body = mainPart.Document.AppendChild(new Body());

                        const string navyHex = "1B365D";
                        const string textHex = "1E293B";
                        const string mutedHex = "475569";
                        const string lightBgHex = "F8FAFC";
                        const string totalBgHex = "EEF2F6";
                        const string borderHex = "CBD5E1";
                        const string accentRedHex = "B91C1C";

                        // === 1. HEADER QUỐC HIỆU & TIÊU NGỮ (2 CỘT CÂN ĐỐI) ===
                        var headerTable = new DocumentFormat.OpenXml.Wordprocessing.Table();
                        headerTable.AppendChild(new TableProperties(
                            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                            CreateNoBorders()
                        ));

                        var headerRow = new TableRow();
                        headerRow.Append(
                            new TableCell(
                                new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2150" }),
                                CreateWordParagraph("BỘ GIÁO DỤC VÀ ĐÀO TẠO", false, JustificationValues.Center, 21, colorHex: textHex, spaceAfter: 40),
                                CreateWordParagraph("TRƯỜNG ĐẠI HỌC VINH", true, JustificationValues.Center, 22, colorHex: navyHex, spaceAfter: 20),
                                CreateWordParagraph("────────────", false, JustificationValues.Center, 14, colorHex: navyHex, spaceAfter: 0)
                            ),
                            new TableCell(
                                new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2850" }),
                                CreateWordParagraph("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", true, JustificationValues.Center, 21, colorHex: textHex, spaceAfter: 40),
                                CreateWordParagraph("Độc lập - Tự do - Hạnh phúc", true, JustificationValues.Center, 22, colorHex: textHex, spaceAfter: 20),
                                CreateWordParagraph("──────────────────", false, JustificationValues.Center, 14, colorHex: textHex, spaceAfter: 0)
                            )
                        );
                        headerTable.Append(headerRow);
                        body.AppendChild(headerTable);

                        // === 2. TIÊU ĐỀ CHÍNH ===
                        body.AppendChild(CreateWordParagraph("PHIẾU TRẢ SÁCH", true, JustificationValues.Center, 34, colorHex: navyHex, spaceBefore: 320, spaceAfter: 40));
                        body.AppendChild(CreateWordParagraph($"Mã phiếu trả: #{returnEntity.ReturnID}   |   Phiếu mượn gốc: #{returnEntity.BorrowingID}", false, JustificationValues.Center, 21, isItalic: true, colorHex: mutedHex, spaceBefore: 0, spaceAfter: 260));

                        // === 3. KHUNG THÔNG TIN PHIẾU TRẢ (2 CỘT CÂN ĐỐI) ===
                        var infoTable = new DocumentFormat.OpenXml.Wordprocessing.Table();
                        infoTable.AppendChild(new TableProperties(
                            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                            new TableBorders(
                                new TopBorder { Val = BorderValues.Single, Size = 6, Color = borderHex },
                                new BottomBorder { Val = BorderValues.Single, Size = 6, Color = borderHex },
                                new LeftBorder { Val = BorderValues.Single, Size = 6, Color = borderHex },
                                new RightBorder { Val = BorderValues.Single, Size = 6, Color = borderHex },
                                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = borderHex },
                                new InsideVerticalBorder { Val = BorderValues.Nil }
                            ),
                            CreateCellMargins(90, 90, 150, 150)
                        ));

                        void AddWordInfoRow(string l1, string v1, string l2, string v2, bool highlight1 = false)
                        {
                            var r = new TableRow();
                            r.Append(
                                CreateWordInfoCell(l1, v1, lightBgHex, mutedHex, highlight1 ? accentRedHex : textHex, highlight1),
                                CreateWordInfoCell(l2, v2, lightBgHex, mutedHex, textHex, false)
                            );
                            infoTable.Append(r);
                        }

                        AddWordInfoRow("Độc giả", patronName, "Trạng thái", statusText);
                        AddWordInfoRow("Email", patronEmail, "Số điện thoại", patronPhone);
                        AddWordInfoRow("Ngày mượn gốc", borrowDateText, "Hạn trả gốc", dueDateText);
                        AddWordInfoRow("Ngày trả thực tế", returnDateText, "Thủ thư nhận trả", librarianName);
                        AddWordInfoRow("Phương thức TT", paymentMethodText, "Mã giao dịch", transactionCodeText);
                        AddWordInfoRow("Tổng tiền phạt", totalFineText, "Thời gian TT", paymentDateText, highlight1: true);

                        body.AppendChild(infoTable);

                        // === 4. BẢNG CHI TIẾT SÁCH TRẢ ===
                        body.AppendChild(CreateWordParagraph("CHI TIẾT SÁCH TRẢ", true, JustificationValues.Left, 23, colorHex: navyHex, spaceBefore: 280, spaceAfter: 120));

                        var dataTable = new DocumentFormat.OpenXml.Wordprocessing.Table();
                        dataTable.AppendChild(new TableProperties(
                            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                            new TableBorders(
                                new TopBorder { Val = BorderValues.Single, Size = 6, Color = borderHex },
                                new BottomBorder { Val = BorderValues.Single, Size = 6, Color = borderHex },
                                new LeftBorder { Val = BorderValues.Single, Size = 6, Color = borderHex },
                                new RightBorder { Val = BorderValues.Single, Size = 6, Color = borderHex },
                                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = borderHex },
                                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = borderHex }
                            ),
                            CreateCellMargins(90, 90, 80, 80)
                        ));

                        var headerRowWord = new TableRow();
                        headerRowWord.Append(
                            CreateWordStyledCell("STT", true, JustificationValues.Center, 19, "FFFFFF", navyHex, widthPct: "300"),
                            CreateWordStyledCell("Mã sách", true, JustificationValues.Center, 19, "FFFFFF", navyHex, widthPct: "450"),
                            CreateWordStyledCell("Tên sách", true, JustificationValues.Center, 19, "FFFFFF", navyHex, widthPct: "1550"),
                            CreateWordStyledCell("Quá hạn", true, JustificationValues.Center, 19, "FFFFFF", navyHex, widthPct: "500"),
                            CreateWordStyledCell("Phạt quá hạn", true, JustificationValues.Center, 19, "FFFFFF", navyHex, widthPct: "750"),
                            CreateWordStyledCell("Tình trạng", true, JustificationValues.Center, 19, "FFFFFF", navyHex, widthPct: "650"),
                            CreateWordStyledCell("Phạt mất/hỏng", true, JustificationValues.Center, 19, "FFFFFF", navyHex, widthPct: "800")
                        );
                        dataTable.Append(headerRowWord);

                        if (details.Any())
                        {
                            int stt = 1;
                            foreach (var detail in details)
                            {
                                string? rowBg = stt % 2 == 0 ? lightBgHex : null;
                                var row = new TableRow();
                                row.Append(
                                    CreateWordStyledCell(stt.ToString(), false, JustificationValues.Center, 19, textHex, rowBg, widthPct: "300"),
                                    CreateWordStyledCell(detail.BookID.ToString(), false, JustificationValues.Center, 19, textHex, rowBg, widthPct: "450"),
                                    CreateWordStyledCell(detail.book?.TitleBook ?? "N/A", false, JustificationValues.Left, 19, textHex, rowBg, widthPct: "1550"),
                                    CreateWordStyledCell($"{detail.OverdueDays} ngày", false, JustificationValues.Center, 19, textHex, rowBg, widthPct: "500"),
                                    CreateWordStyledCell($"{detail.OverdueFine:N0} VNĐ", false, JustificationValues.Right, 19, textHex, rowBg, widthPct: "750"),
                                    CreateWordStyledCell(detail.IsDamaged ? "Hư hỏng/Mất" : "Bình thường", false, JustificationValues.Center, 19, textHex, rowBg, widthPct: "650"),
                                    CreateWordStyledCell($"{detail.LostFine:N0} VNĐ", false, JustificationValues.Right, 19, textHex, rowBg, widthPct: "800")
                                );
                                dataTable.Append(row);
                                stt++;
                            }
                        }
                        else
                        {
                            var emptyRow = new TableRow();
                            emptyRow.Append(CreateWordStyledCell("Chưa có sách nào trong phiếu trả này.", false, JustificationValues.Center, 20, mutedHex, null, gridSpan: 7, isItalic: true));
                            dataTable.Append(emptyRow);
                        }

                        // Hàng tổng cộng
                        var totalRow = new TableRow();
                        totalRow.Append(
                            CreateWordStyledCell($"Tổng cộng ({details.Count} cuốn):", true, JustificationValues.Right, 19, navyHex, totalBgHex, gridSpan: 4),
                            CreateWordStyledCell($"{totalOverdueFine:N0} VNĐ", true, JustificationValues.Right, 19, textHex, totalBgHex, widthPct: "750"),
                            CreateWordStyledCell("Tổng phạt:", true, JustificationValues.Center, 19, navyHex, totalBgHex, widthPct: "650"),
                            CreateWordStyledCell(totalFineText, true, JustificationValues.Right, 19, accentRedHex, totalBgHex, widthPct: "800")
                        );
                        dataTable.Append(totalRow);

                        body.AppendChild(dataTable);

                        // === 5. PHẦN CHỮ KÝ (CÂN ĐỐI 2 BÊN) ===
                        body.AppendChild(CreateWordParagraph(dateText, false, JustificationValues.Right, 21, isItalic: true, colorHex: textHex, spaceBefore: 360, spaceAfter: 140));

                        var sigTable = new DocumentFormat.OpenXml.Wordprocessing.Table();
                        sigTable.AppendChild(new TableProperties(
                            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                            CreateNoBorders()
                        ));

                        var sigRow = new TableRow();
                        sigRow.Append(
                            CreateWordSignatureCell("ĐỘC GIẢ", patronName, navyHex, mutedHex, textHex),
                            CreateWordSignatureCell("THỦ THƯ NHẬN TRẢ", librarianName, navyHex, mutedHex, textHex)
                        );
                        sigTable.Append(sigRow);
                        body.AppendChild(sigTable);

                        // Khổ giấy A4 & lề cân đối
                        body.AppendChild(new SectionProperties(
                            new PageSize { Width = 11906, Height = 16838 },
                            new PageMargin { Top = 850, Bottom = 850, Left = 920, Right = 920 }
                        ));
                    }

                    memoryStream.Seek(0, SeekOrigin.Begin);
                    return File(memoryStream.ToArray(), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"PhieuTra_{id}.docx");
                }
                else
                {
                    return BadRequest("Định dạng không hợp lệ. Chỉ hỗ trợ pdf hoặc word.");
                }
            }
        }


        [HttpGet]
        public async Task<IActionResult> PaymentQr(int id)
        {
            try
            {
                var returnModel = await _context.Returns
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.ReturnID == id);

                if (returnModel == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy phiếu trả." });
                }

                await EnsurePaymentHistoryTableAsync();
                bool alreadyPaid = await _context.PaymentHistories
                    .AnyAsync(p => p.ReturnID == returnModel.ReturnID && p.PaymentStatus == "Đã thanh toán");

                if (alreadyPaid)
                {
                    return Json(new
                    {
                        success = false,
                        message = "Phiếu trả này đã thanh toán thành công, không thể thực hiện thanh toán nữa."
                    });
                }

                var setting = await _feePaymentService.GetActiveSettingAsync();
                var amount = returnModel.TotalFine < 0 ? 0 : returnModel.TotalFine;
                var addInfo = $"THANH TOAN PHAT RT{returnModel.ReturnID}";
                var imageUrl = await _feePaymentService.BuildVietQrImageUrlAsync(amount, addInfo);
                var payload = _feePaymentService.BuildVietQrPayload(
                    setting.BankBin ?? "970415",
                    setting.BankAccountNumber ?? "12345",
                    amount,
                    addInfo,
                    setting.BankAccountName ?? "VIETINBANK");

                return Json(new
                {
                    success = true,
                    returnId = returnModel.ReturnID,
                    amount,
                    bankName = setting.BankName,
                    bankBin = setting.BankBin,
                    accountNumber = setting.BankAccountNumber,
                    accountName = setting.BankAccountName,
                    addInfo,
                    imageUrl,
                    payload
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi khi tạo mã QR: " + ex.Message });
            }
        }
        private static TableBorders CreateNoBorders()
        {
            return new TableBorders(
                new TopBorder { Val = BorderValues.Nil },
                new BottomBorder { Val = BorderValues.Nil },
                new LeftBorder { Val = BorderValues.Nil },
                new RightBorder { Val = BorderValues.Nil },
                new InsideHorizontalBorder { Val = BorderValues.Nil },
                new InsideVerticalBorder { Val = BorderValues.Nil }
            );
        }

        private static TableCellMarginDefault CreateCellMargins(int top, int bottom, int left, int right)
        {
            return new TableCellMarginDefault(
                new TopMargin { Width = top.ToString(), Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = bottom.ToString(), Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = (short)left, Type = TableWidthValues.Dxa },
                new TableCellRightMargin { Width = (short)right, Type = TableWidthValues.Dxa }
            );
        }

        private static DocumentFormat.OpenXml.Wordprocessing.Paragraph CreateWordParagraph(
            string text,
            bool isBold = false,
            JustificationValues? align = null,
            int fontSizeHalfPts = 21,
            bool isItalic = false,
            string colorHex = "1E293B",
            int spaceBefore = 0,
            int spaceAfter = 0)
        {
            var para = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new ParagraphProperties(
                    new Justification { Val = align ?? JustificationValues.Left },
                    new SpacingBetweenLines { Before = spaceBefore.ToString(), After = spaceAfter.ToString() }
                )
            );

            var rPr = new RunProperties(
                new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
                new FontSize { Val = fontSizeHalfPts.ToString() },
                new FontSizeComplexScript { Val = fontSizeHalfPts.ToString() },
                new Color { Val = colorHex }
            );
            if (isBold) rPr.AppendChild(new Bold());
            if (isItalic) rPr.AppendChild(new Italic());

            var run = new Run(rPr, new DocumentFormat.OpenXml.Wordprocessing.Text(text) { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(run);
            return para;
        }

        private static TableCell CreateWordInfoCell(string label, string value, string fillHex, string labelColorHex, string valueColorHex, bool boldValue)
        {
            var para = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new ParagraphProperties(
                    new SpacingBetweenLines { Before = "0", After = "0" }
                )
            );

            var labelRun = new Run(
                new RunProperties(
                    new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
                    new FontSize { Val = "20" },
                    new Bold(),
                    new Color { Val = labelColorHex }
                ),
                new DocumentFormat.OpenXml.Wordprocessing.Text(label + ": ") { Space = SpaceProcessingModeValues.Preserve }
            );

            var valueProps = new RunProperties(
                new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
                new FontSize { Val = "20" },
                new Color { Val = valueColorHex }
            );
            if (boldValue) valueProps.AppendChild(new Bold());

            var valueRun = new Run(
                valueProps,
                new DocumentFormat.OpenXml.Wordprocessing.Text(value) { Space = SpaceProcessingModeValues.Preserve }
            );

            para.Append(labelRun, valueRun);

            return new TableCell(
                new TableCellProperties(
                    new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2500" },
                    new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fillHex },
                    new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }
                ),
                para
            );
        }

        private static TableCell CreateWordStyledCell(
            string text,
            bool isBold,
            JustificationValues align,
            int fontSizeHalfPts,
            string colorHex,
            string? fillHex,
            string? widthPct = null,
            int gridSpan = 1,
            bool isItalic = false)
        {
            var tcPr = new TableCellProperties(
                new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }
            );

            if (!string.IsNullOrEmpty(widthPct))
                tcPr.AppendChild(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = widthPct });
            if (gridSpan > 1)
                tcPr.AppendChild(new GridSpan { Val = gridSpan });
            if (!string.IsNullOrEmpty(fillHex))
                tcPr.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fillHex });

            return new TableCell(
                tcPr,
                CreateWordParagraph(text, isBold, align, fontSizeHalfPts, isItalic, colorHex)
            );
        }

        private static TableCell CreateWordSignatureCell(string roleTitle, string signerName, string navyHex, string mutedHex, string textHex)
        {
            return new TableCell(
                new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2500" }),
                CreateWordParagraph(roleTitle, true, JustificationValues.Center, 22, colorHex: navyHex, spaceAfter: 40),
                CreateWordParagraph("(Ký, ghi rõ họ tên)", false, JustificationValues.Center, 19, isItalic: true, colorHex: mutedHex, spaceAfter: 900),
                CreateWordParagraph(signerName, true, JustificationValues.Center, 21, colorHex: textHex)
            );
        }
    }
}
