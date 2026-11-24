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
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Kernel.Pdf;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.IO;
using iText.Kernel.Font;
using iText.IO.Font.Constants;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class BorrowingController : Controller
    {
        private readonly DataContext _context;
        public BorrowingController(DataContext context)
        {
            _context = context;
        }





        [HttpGet]
        public async Task<IActionResult> Index(string? search)
        {
            // Truy vấn danh sách phiếu mượn
            var query = _context.Borrowings
                .Include(h => h.patron)
                .Include(h => h.librarian)
                .AsQueryable();

            // Tìm kiếm theo tên hoặc email độc giả
            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim();
                bool isEmailSearch = search.Contains('@');
                string emailPattern = isEmailSearch ? $"%{search}%" : $"%{search}%@%";

                // Tìm danh sách mã độc giả phù hợp với từ khóa
                // Nếu từ khóa không chứa '@', chỉ tìm ở phần trước '@' của email để tránh khớp nhầm tên miền (vd: "Mai" khớp "@gmail.com")
                var patronIDs = await _context.Patrons
                    .Where(p =>
                        EF.Functions.Like(p.PatronName, $"%{search}%") ||
                        (p.Email != null &&
                         EF.Functions.Like(p.Email, emailPattern))
                    )
                    .Select(p => p.PatronID)
                    .ToListAsync();

                // Lọc phiếu mượn theo danh sách mã độc giả
                query = query.Where(h =>
                    patronIDs.Contains(h.PatronID));
            }

            // Sắp xếp phiếu mượn mới nhất lên đầu
            var borrowingList = await query
                .OrderByDescending(h => h.BorrowingID)
                .ToListAsync();

            // Đồng bộ hiển thị trạng thái phiếu mượn theo trạng thái thanh toán của phiếu trả:
            // Nếu phiếu trả tương ứng "Chưa hoàn tất" (chưa có lịch sử thanh toán), giữ nguyên trạng thái phiếu mượn (Đang mượn / Quá hạn).
            // Chỉ khi phiếu trả "Đã hoàn tất" (đã lưu lịch sử thanh toán) mới hiển thị "Đã trả".
            try
            {
                var paidBorrowingIds = (await _context.PaymentHistories
                    .AsNoTracking()
                    .Where(p => p.PaymentStatus == "Đã thanh toán" || p.PaymentStatus == "Thu dư")
                    .Select(p => p.BorrowingID)
                    .Distinct()
                    .ToListAsync())
                    .ToHashSet();

                foreach (var b in borrowingList)
                {
                    b.IsActive = !paidBorrowingIds.Contains(b.BorrowingID);
                }
            }
            catch
            {
                // Bảng tblPaymentHistory chưa được tạo -> giữ nguyên trạng thái hiện tại
            }

            // Giữ lại từ khóa tìm kiếm trên giao diện
            ViewBag.Search = search;

            return View(borrowingList);
        }

        [HttpGet]
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var borrowing = await _context.Borrowings
                .Include(b => b.patron)
                .Include(b => b.librarian)
                .Include(b => b.borrowingDetail)
                    .ThenInclude(bd => bd.book)
                .FirstOrDefaultAsync(b => b.BorrowingID == id);

            if (borrowing == null)
                return NotFound();

            return View(borrowing);
        }

        // Trong file BorrowingController.cs
        public IActionResult Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            // Thay đổi: Sử dụng FirstOrDefault() và Include để nạp thông tin Độc giả và Thủ thư
            var sb = _context.Borrowings
                .Include(b => b.patron)
                .Include(b => b.librarian)
                .FirstOrDefault(b => b.BorrowingID == id);

            if (sb == null)
                return NotFound();
            return View(sb);
        }
        [HttpPost]

        [HttpPost, ActionName("Delete")] // Đảm bảo ActionName là Delete
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            // 1. Nạp phiếu mượn cha và tất cả chi tiết mượn con liên quan
            var delBorrowing = await _context.Borrowings
                .Include(b => b.borrowingDetail) // Include các chi tiết phiếu mượn
                .FirstOrDefaultAsync(b => b.BorrowingID == id);

            if (delBorrowing == null)
            {
                TempData["Error"] = "Phiếu mượn không tồn tại.";
                return RedirectToAction(nameof(Index));
            }

            // 2. Xử lý các chi tiết phiếu mượn con và cập nhật BorrowQuantity của sách
            if (delBorrowing.borrowingDetail != null && delBorrowing.borrowingDetail.Any())
            {
                var bookIDsToUpdate = new List<int>();

                foreach (var detail in delBorrowing.borrowingDetail)
                {
                    // Nếu chi tiết mượn đang Hoạt động (IsActive = true), phải giảm BorrowQuantity của sách
                    if (detail.IsActive)
                    {
                        bookIDsToUpdate.Add(detail.BookID);
                    }
                }

                // Lấy danh sách sách cần cập nhật BorrowQuantity (đảm bảo chỉ lấy 1 lần)
                var books = await _context.Books
                    .Where(b => bookIDsToUpdate.Contains(b.BookID))
                    .ToListAsync();

                foreach (var book in books)
                {
                    // Giảm BorrowQuantity của sách. Sử dụng Math.Max để đảm bảo không âm.
                    book.BorrowQuantity = Math.Max(0, book.BorrowQuantity - bookIDsToUpdate.Count(id => id == book.BookID));
                    _context.Books.Update(book);
                }

                // Entity Framework Core sẽ tự động xóa các chi tiết con khi xóa phiếu mượn cha (Cascade Delete) 
                // nếu đã cấu hình quan hệ (Relationship) đúng trong DataContext.
                // Nếu chưa cấu hình, cần thêm: _context.BorrowingDetails.RemoveRange(delBorrowing.borrowingDetail);
            }

            // 3. Xóa phiếu mượn
            _context.Borrowings.Remove(delBorrowing);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã xóa thành công Phiếu mượn ID: {id} và các chi tiết liên quan. Số lượng sách mượn đã được cập nhật.";
            return RedirectToAction(nameof(Index));
        }

        public IActionResult Create()
        {
            LoadData();
            return View();
        }
        [HttpPost]
        public IActionResult Create(tblBorrowing sb, int? BookID)
        {
            if (sb.PatronID <= 0)
            {
                ModelState.AddModelError("PatronID", "Vui lòng chọn độc giả.");
            }

            // **TỰ ĐỘNG GÁN BorrowDate và CreateDate**
            sb.BorrowDate = DateTime.Now;
            sb.CreateDate = DateTime.Now;
            sb.IsActive = true;

            // **KIỂM TRA ĐỘC GIẢ CÓ PHIẾU MƯỢN QUÁ HẠN KHÔNG**
            bool hasOverdueBorrowing = _context.Borrowings
                .Any(b => b.PatronID == sb.PatronID
                       && b.IsActive == true
                       && b.DueDate.HasValue
                       && b.DueDate.Value < DateTime.Now);

            if (hasOverdueBorrowing)
            {
                ModelState.AddModelError("PatronID",
                    "Độc giả này đang có phiếu mượn quá hạn. Vui lòng xử lý phiếu quá hạn trước khi tạo phiếu mượn mới.");
            }

            // **KIỂM TRA HẠN TRẢ PHẢI LỚN HƠN HOẶC BẰNG NGÀY MƯỢN**
            if (!sb.DueDate.HasValue || sb.DueDate.Value < sb.BorrowDate.Value)
            {
                ModelState.AddModelError("DueDate", "Hạn trả phải lớn hơn hoặc bằng ngày mượn.");
            }

            if (ModelState.IsValid)
            {
                _context.Borrowings.Add(sb);
                _context.SaveChanges();

                TempData["Success"] = "Thêm phiếu mượn thành công!";
                return RedirectToAction("Index");
            }

            // Nếu có lỗi → load lại dữ liệu cho dropdown
            LoadData();
            return View(sb);
        }
        public IActionResult Edit(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var mn = _context.Borrowings
                .Include(b => b.patron)      // ← Thêm dòng này để load thông tin độc giả
                .Include(b => b.librarian)   // ← (Tùy chọn) Load luôn thủ thư để hiển thị tên nếu cần
                .FirstOrDefault(b => b.BorrowingID == id);

            if (mn == null)
                return NotFound();

            LoadData(); // Để load danh sách thủ thư cho select
            return View(mn);
        }
        [HttpPost]
        public IActionResult Edit(tblBorrowing sb)
        {
            // Lấy dữ liệu gốc để giữ nguyên các trường không được sửa
            var original = _context.Borrowings.AsNoTracking()
                .FirstOrDefault(b => b.BorrowingID == sb.BorrowingID);

            if (original == null)
                return NotFound();

            // Gán lại các trường không được phép thay đổi
            sb.BorrowDate = original.BorrowDate;
            sb.CreateDate = original.CreateDate;
            sb.UpdateDate = DateTime.Now;

            // **KIỂM TRA HẠN TRẢ TRƯỚC KHI LƯU**
            if (sb.DueDate < sb.BorrowDate)
            {
                ModelState.AddModelError("DueDate", "Hạn trả phải lớn hơn hoặc bằng ngày mượn.");
            }

            // **CHỈ LƯU KHI KHÔNG CÓ LỖI**
            if (!ModelState.IsValid)
            {
                // Load lại thông tin độc giả và thủ thư để hiển thị đúng tên
                _context.Entry(sb).Reference(b => b.patron).Load();
                _context.Entry(sb).Reference(b => b.librarian).Load();

                LoadData(); // Load dropdown thủ thư
                return View(sb);
            }

            // Nếu hợp lệ → mới lưu
            _context.Borrowings.Update(sb);
            _context.SaveChanges();

            UpdateTotalBorrowingFeeAsync(sb.BorrowingID).Wait();

            TempData["Success"] = "Cập nhật phiếu mượn thành công!";
            return RedirectToAction("Index");
        }
        private void LoadData()
        {
            // Lấy TẤT CẢ độc giả đang hoạt động (không lọc theo phiếu mượn)
            var ptList = _context.Patrons
                .Where(p => p.IsActive == true)
                .Select(pt => new
                {
                    pt.PatronID,
                    Info = pt.PatronID + " - " + pt.PatronName
                })
                .OrderBy(x => x.PatronID)
                .ToList();

            ViewBag.ptList = new SelectList(ptList, "PatronID", "Info");

            // Thủ thư giữ nguyên
            var laList = _context.Librarians
                .Where(l => l.IsActive == true)
                .Select(la => new
                {
                    la.LibrarianID,
                    Info = la.LibrarianID + " - " + la.FullName
                })
                .OrderBy(x => x.LibrarianID)
                .ToList();

            ViewBag.laList = new SelectList(laList, "LibrarianID", "Info");
        }
        private async Task UpdateTotalBorrowingFeeAsync(int borrowingId)
        {
            var total = await _context.BorrowingDetails
                .Where(bd => bd.BorrowingID == borrowingId && bd.IsActive)
                .SumAsync(bd => (decimal?)bd.BorrowingFee) ?? 0;  // Dùng nullable để tránh lỗi nếu không có bản ghi

            var borrowing = await _context.Borrowings.FirstOrDefaultAsync(b => b.BorrowingID == borrowingId);
            if (borrowing != null)
            {
                borrowing.TotalBorrowingFee = total;
                _context.Borrowings.Update(borrowing);
                await _context.SaveChangesAsync();  // QUAN TRỌNG: Phải SaveChanges ở đây!
            }
        }

        // Action mới: Xuất phiếu mượn
        public async Task<IActionResult> Export(int id, string format)
        {
            // Lấy phiếu mượn với đầy đủ thông tin
            var borrowing = await _context.Borrowings
                .Include(b => b.patron)
                .Include(b => b.librarian)
                .Include(b => b.borrowingDetail)
                .ThenInclude(bd => bd.book)
                .FirstOrDefaultAsync(b => b.BorrowingID == id);

            if (borrowing == null)
            {
                return NotFound();
            }

            var details = borrowing.borrowingDetail?.ToList() ?? new List<tblBorrowingDetail>();
            var now = DateTime.Now;
            string dateText = $"TP. Vinh, ngày {now.Day:D2} tháng {now.Month:D2} năm {now.Year}";
            string patronName = borrowing.patron?.PatronName ?? "N/A";
            string patronEmail = string.IsNullOrWhiteSpace(borrowing.patron?.Email) ? "-" : borrowing.patron!.Email!;
            string patronPhone = string.IsNullOrWhiteSpace(borrowing.patron?.Phone) ? "-" : borrowing.patron!.Phone!;
            string librarianName = borrowing.librarian?.FullName ?? "N/A";
            string borrowDateText = borrowing.BorrowDate?.ToString("dd/MM/yyyy HH:mm") ?? "-";
            string dueDateText = borrowing.DueDate?.ToString("dd/MM/yyyy HH:mm") ?? "-";
            string createDateText = borrowing.CreateDate?.ToString("dd/MM/yyyy HH:mm") ?? "-";
            string updateDateText = borrowing.UpdateDate?.ToString("dd/MM/yyyy HH:mm") ?? "-";
            string totalFeeText = $"{borrowing.TotalBorrowingFee:N0} VNĐ";

            // Tạo MemoryStream để lưu file
            using (var memoryStream = new MemoryStream())
            {
                if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
                {
                    // Tạo PDF bằng iText7
                    PdfWriter writer = new PdfWriter(memoryStream);
                    PdfDocument pdf = new PdfDocument(writer);
                    pdf.SetDefaultPageSize(iText.Kernel.Geom.PageSize.A4);
                    iText.Layout.Document document = new iText.Layout.Document(pdf);
                    document.SetMargins(38, 42, 38, 42);

                    // Đường dẫn font
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

                    // Bảng màu thống nhất
                    var navyColor = new iText.Kernel.Colors.DeviceRgb(27, 54, 93);       // #1B365D
                    var textColor = new iText.Kernel.Colors.DeviceRgb(30, 41, 59);       // #1E293B
                    var mutedColor = new iText.Kernel.Colors.DeviceRgb(71, 85, 105);     // #475569
                    var lightBgColor = new iText.Kernel.Colors.DeviceRgb(248, 250, 252); // #F8FAFC
                    var totalBgColor = new iText.Kernel.Colors.DeviceRgb(238, 242, 246); // #EEF2F6
                    var borderColor = new iText.Kernel.Colors.DeviceRgb(203, 213, 225);  // #CBD5E1
                    var accentRed = new iText.Kernel.Colors.DeviceRgb(185, 28, 28);      // #B91C1C
                    var thinBorder = new iText.Layout.Borders.SolidBorder(borderColor, 0.75f);

                    // === 1. HEADER QUỐC HIỆU & TIÊU NGỮ (CÂN ĐỐI 2 CỘT) ===
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
                    document.Add(new iText.Layout.Element.Paragraph("PHIẾU MƯỢN SÁCH")
                        .SetFont(boldFont)
                        .SetFontSize(17)
                        .SetFontColor(navyColor)
                        .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                        .SetMarginTop(6)
                        .SetMarginBottom(2));

                    document.Add(new iText.Layout.Element.Paragraph($"Mã phiếu: #{borrowing.BorrowingID}")
                        .SetFont(italicFont)
                        .SetFontSize(10.5f)
                        .SetFontColor(mutedColor)
                        .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                        .SetMarginTop(0)
                        .SetMarginBottom(16));

                    // === 3. KHUNG THÔNG TIN PHIẾU (2 CỘT CÂN ĐỐI) ===
                    var infoTable = new iText.Layout.Element.Table(UnitValue.CreatePercentArray(new float[] { 50, 50 }))
                        .UseAllAvailableWidth()
                        .SetBackgroundColor(lightBgColor)
                        .SetBorder(thinBorder)
                        .SetMarginBottom(18);

                    Cell CreatePdfInfoCell(string label, string value, bool highlightValue = false)
                    {
                        var p = new iText.Layout.Element.Paragraph()
                            .SetMargin(0)
                            .SetMultipliedLeading(1.2f)
                            .Add(new iText.Layout.Element.Text(label + ": ")
                                .SetFont(boldFont).SetFontSize(10.5f).SetFontColor(mutedColor))
                            .Add(new iText.Layout.Element.Text(value)
                                .SetFont(highlightValue ? boldFont : normalFont)
                                .SetFontSize(10.5f)
                                .SetFontColor(highlightValue ? accentRed : textColor));

                        return new Cell()
                            .SetBorder(iText.Layout.Borders.Border.NO_BORDER)
                            .SetBorderBottom(new iText.Layout.Borders.SolidBorder(borderColor, 0.4f))
                            .SetPaddingTop(6)
                            .SetPaddingBottom(6)
                            .SetPaddingLeft(10)
                            .SetPaddingRight(10)
                            .Add(p);
                    }

                    infoTable.AddCell(CreatePdfInfoCell("Độc giả", patronName));
                    infoTable.AddCell(CreatePdfInfoCell("Trạng thái", borrowing.BorrowingStatusDisplay));
                    infoTable.AddCell(CreatePdfInfoCell("Email", patronEmail));
                    infoTable.AddCell(CreatePdfInfoCell("Số điện thoại", patronPhone));
                    infoTable.AddCell(CreatePdfInfoCell("Thủ thư tạo", librarianName));
                    infoTable.AddCell(CreatePdfInfoCell("Tổng phí mượn", totalFeeText, highlightValue: true));
                    infoTable.AddCell(CreatePdfInfoCell("Ngày mượn", borrowDateText));
                    infoTable.AddCell(CreatePdfInfoCell("Hạn trả", dueDateText));
                    infoTable.AddCell(CreatePdfInfoCell("Ngày tạo", createDateText).SetBorderBottom(iText.Layout.Borders.Border.NO_BORDER));
                    infoTable.AddCell(CreatePdfInfoCell("Ngày cập nhật", updateDateText).SetBorderBottom(iText.Layout.Borders.Border.NO_BORDER));

                    document.Add(infoTable);

                    // === 4. BẢNG CHI TIẾT SÁCH MƯỢN ===
                    document.Add(new iText.Layout.Element.Paragraph("CHI TIẾT SÁCH MƯỢN")
                        .SetFont(boldFont)
                        .SetFontSize(11.5f)
                        .SetFontColor(navyColor)
                        .SetMarginTop(0)
                        .SetMarginBottom(8));

                    var table = new iText.Layout.Element.Table(UnitValue.CreatePercentArray(new float[] { 8, 12, 44, 18, 18 }))
                        .UseAllAvailableWidth();

                    string[] headers = { "STT", "Mã sách", "Tên sách", "Phí mượn", "Trạng thái" };
                    foreach (var h in headers)
                    {
                        table.AddHeaderCell(new Cell()
                            .SetBackgroundColor(navyColor)
                            .SetBorder(thinBorder)
                            .SetPaddingTop(7)
                            .SetPaddingBottom(7)
                            .SetPaddingLeft(6)
                            .SetPaddingRight(6)
                            .SetVerticalAlignment(iText.Layout.Properties.VerticalAlignment.MIDDLE)
                            .Add(new iText.Layout.Element.Paragraph(h)
                                .SetFont(boldFont)
                                .SetFontSize(10.5f)
                                .SetFontColor(iText.Kernel.Colors.ColorConstants.WHITE)
                                .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                                .SetMargin(0)));
                    }

                    if (details.Any())
                    {
                        int stt = 1;
                        foreach (var detail in details)
                        {
                            bool isEven = stt % 2 == 0;
                            var rowBg = isEven ? lightBgColor : iText.Kernel.Colors.ColorConstants.WHITE;

                            Cell CreatePdfDataCell(string text, iText.Layout.Properties.TextAlignment align)
                            {
                                return new Cell()
                                    .SetBackgroundColor(rowBg)
                                    .SetBorder(thinBorder)
                                    .SetPaddingTop(6)
                                    .SetPaddingBottom(6)
                                    .SetPaddingLeft(7)
                                    .SetPaddingRight(7)
                                    .SetVerticalAlignment(iText.Layout.Properties.VerticalAlignment.MIDDLE)
                                    .Add(new iText.Layout.Element.Paragraph(text)
                                        .SetFont(normalFont)
                                        .SetFontSize(10.5f)
                                        .SetFontColor(textColor)
                                        .SetTextAlignment(align)
                                        .SetMargin(0));
                            }

                            table.AddCell(CreatePdfDataCell(stt.ToString(), iText.Layout.Properties.TextAlignment.CENTER));
                            table.AddCell(CreatePdfDataCell(detail.BookID.ToString(), iText.Layout.Properties.TextAlignment.CENTER));
                            table.AddCell(CreatePdfDataCell(detail.book?.TitleBook ?? "N/A", iText.Layout.Properties.TextAlignment.LEFT));
                            table.AddCell(CreatePdfDataCell($"{detail.BorrowingFee:N0} VNĐ", iText.Layout.Properties.TextAlignment.RIGHT));
                            table.AddCell(CreatePdfDataCell(detail.IsActive ? "Đang mượn" : "Đã trả", iText.Layout.Properties.TextAlignment.CENTER));
                            stt++;
                        }
                    }
                    else
                    {
                        table.AddCell(new Cell(1, 5)
                            .SetBorder(thinBorder)
                            .SetPadding(10)
                            .Add(new iText.Layout.Element.Paragraph("Chưa có sách nào trong phiếu mượn này.")
                                .SetFont(italicFont)
                                .SetFontSize(10.5f)
                                .SetFontColor(mutedColor)
                                .SetTextAlignment(iText.Layout.Properties.TextAlignment.CENTER)
                                .SetMargin(0)));
                    }

                    // Hàng tổng cộng
                    table.AddCell(new Cell(1, 3)
                        .SetBackgroundColor(totalBgColor)
                        .SetBorder(thinBorder)
                        .SetPaddingTop(7).SetPaddingBottom(7).SetPaddingRight(8)
                        .Add(new iText.Layout.Element.Paragraph($"Tổng cộng ({details.Count} cuốn):")
                            .SetFont(boldFont).SetFontSize(10.5f).SetFontColor(navyColor)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.RIGHT)
                            .SetMargin(0)));

                    table.AddCell(new Cell()
                        .SetBackgroundColor(totalBgColor)
                        .SetBorder(thinBorder)
                        .SetPaddingTop(7).SetPaddingBottom(7).SetPaddingRight(7)
                        .Add(new iText.Layout.Element.Paragraph(totalFeeText)
                            .SetFont(boldFont).SetFontSize(10.5f).SetFontColor(accentRed)
                            .SetTextAlignment(iText.Layout.Properties.TextAlignment.RIGHT)
                            .SetMargin(0)));

                    table.AddCell(new Cell()
                        .SetBackgroundColor(totalBgColor)
                        .SetBorder(thinBorder)
                        .SetPadding(6));

                    document.Add(table);

                    // === 5. PHẦN CHỮ KÝ (CÂN ĐỐI 2 BÊN: ĐỘC GIẢ & THỦ THƯ) ===
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
                    signatureTable.AddCell(CreatePdfSignatureCell("THỦ THƯ TẠO PHIẾU", librarianName));

                    document.Add(signatureTable);

                    document.Close();

                    return File(memoryStream.ToArray(), "application/pdf", $"PhieuMuon_{id}.pdf");
                }
                else if (string.Equals(format, "word", StringComparison.OrdinalIgnoreCase))
                {
                    using (WordprocessingDocument wordDoc = WordprocessingDocument.Create(memoryStream, WordprocessingDocumentType.Document))
                    {
                        MainDocumentPart mainPart = wordDoc.AddMainDocumentPart();
                        mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
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

                        var leftHeaderCell = new TableCell(
                            new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2150" }),
                            CreateWordParagraph("BỘ GIÁO DỤC VÀ ĐÀO TẠO", false, JustificationValues.Center, 21, colorHex: textHex, spaceAfter: 40),
                            CreateWordParagraph("TRƯỜNG ĐẠI HỌC VINH", true, JustificationValues.Center, 22, colorHex: navyHex, spaceAfter: 20),
                            CreateWordParagraph("────────────", false, JustificationValues.Center, 14, colorHex: navyHex, spaceAfter: 0)
                        );

                        var rightHeaderCell = new TableCell(
                            new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2850" }),
                            CreateWordParagraph("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", true, JustificationValues.Center, 21, colorHex: textHex, spaceAfter: 40),
                            CreateWordParagraph("Độc lập - Tự do - Hạnh phúc", true, JustificationValues.Center, 22, colorHex: textHex, spaceAfter: 20),
                            CreateWordParagraph("──────────────────", false, JustificationValues.Center, 14, colorHex: textHex, spaceAfter: 0)
                        );

                        headerRow.Append(leftHeaderCell, rightHeaderCell);
                        headerTable.Append(headerRow);
                        body.AppendChild(headerTable);

                        // === 2. TIÊU ĐỀ CHÍNH ===
                        body.AppendChild(CreateWordParagraph("PHIẾU MƯỢN SÁCH", true, JustificationValues.Center, 34, colorHex: navyHex, spaceBefore: 320, spaceAfter: 40));
                        body.AppendChild(CreateWordParagraph($"Mã phiếu: #{borrowing.BorrowingID}", false, JustificationValues.Center, 21, isItalic: true, colorHex: mutedHex, spaceBefore: 0, spaceAfter: 260));

                        // === 3. KHUNG THÔNG TIN PHIẾU (2 CỘT CÂN ĐỐI) ===
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
                            CreateCellMargins(100, 100, 160, 160)
                        ));

                        void AddWordInfoRow(string l1, string v1, string l2, string v2, bool highlight2 = false)
                        {
                            var r = new TableRow();
                            r.Append(
                                CreateWordInfoCell(l1, v1, lightBgHex, mutedHex, textHex, false),
                                CreateWordInfoCell(l2, v2, lightBgHex, mutedHex, highlight2 ? accentRedHex : textHex, highlight2)
                            );
                            infoTable.Append(r);
                        }

                        AddWordInfoRow("Độc giả", patronName, "Trạng thái", borrowing.BorrowingStatusDisplay);
                        AddWordInfoRow("Email", patronEmail, "Số điện thoại", patronPhone);
                        AddWordInfoRow("Thủ thư tạo", librarianName, "Tổng phí mượn", totalFeeText, highlight2: true);
                        AddWordInfoRow("Ngày mượn", borrowDateText, "Hạn trả", dueDateText);
                        AddWordInfoRow("Ngày tạo", createDateText, "Ngày cập nhật", updateDateText);

                        body.AppendChild(infoTable);

                        // === 4. BẢNG CHI TIẾT SÁCH MƯỢN ===
                        body.AppendChild(CreateWordParagraph("CHI TIẾT SÁCH MƯỢN", true, JustificationValues.Left, 23, colorHex: navyHex, spaceBefore: 280, spaceAfter: 120));

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
                            CreateCellMargins(100, 100, 110, 110)
                        ));

                        // Header bảng
                        var headerRowWord = new TableRow();
                        headerRowWord.Append(
                            CreateWordStyledCell("STT", true, JustificationValues.Center, 21, "FFFFFF", navyHex, widthPct: "400"),
                            CreateWordStyledCell("Mã sách", true, JustificationValues.Center, 21, "FFFFFF", navyHex, widthPct: "600"),
                            CreateWordStyledCell("Tên sách", true, JustificationValues.Center, 21, "FFFFFF", navyHex, widthPct: "2200"),
                            CreateWordStyledCell("Phí mượn", true, JustificationValues.Center, 21, "FFFFFF", navyHex, widthPct: "900"),
                            CreateWordStyledCell("Trạng thái", true, JustificationValues.Center, 21, "FFFFFF", navyHex, widthPct: "900")
                        );
                        dataTable.Append(headerRowWord);

                        // Dữ liệu bảng
                        if (details.Any())
                        {
                            int stt = 1;
                            foreach (var detail in details)
                            {
                                string? rowBg = stt % 2 == 0 ? lightBgHex : null;
                                var row = new TableRow();
                                row.Append(
                                    CreateWordStyledCell(stt.ToString(), false, JustificationValues.Center, 21, textHex, rowBg, widthPct: "400"),
                                    CreateWordStyledCell(detail.BookID.ToString(), false, JustificationValues.Center, 21, textHex, rowBg, widthPct: "600"),
                                    CreateWordStyledCell(detail.book?.TitleBook ?? "N/A", false, JustificationValues.Left, 21, textHex, rowBg, widthPct: "2200"),
                                    CreateWordStyledCell($"{detail.BorrowingFee:N0} VNĐ", false, JustificationValues.Right, 21, textHex, rowBg, widthPct: "900"),
                                    CreateWordStyledCell(detail.IsActive ? "Đang mượn" : "Đã trả", false, JustificationValues.Center, 21, textHex, rowBg, widthPct: "900")
                                );
                                dataTable.Append(row);
                                stt++;
                            }
                        }
                        else
                        {
                            var emptyRow = new TableRow();
                            emptyRow.Append(CreateWordStyledCell("Chưa có sách nào trong phiếu mượn này.", false, JustificationValues.Center, 21, mutedHex, null, gridSpan: 5, isItalic: true));
                            dataTable.Append(emptyRow);
                        }

                        // Hàng tổng cộng
                        var totalRow = new TableRow();
                        totalRow.Append(
                            CreateWordStyledCell($"Tổng cộng ({details.Count} cuốn):", true, JustificationValues.Right, 21, navyHex, totalBgHex, gridSpan: 3),
                            CreateWordStyledCell(totalFeeText, true, JustificationValues.Right, 21, accentRedHex, totalBgHex, widthPct: "900"),
                            CreateWordStyledCell(string.Empty, false, JustificationValues.Center, 21, textHex, totalBgHex, widthPct: "900")
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
                            CreateWordSignatureCell("THỦ THƯ TẠO PHIẾU", librarianName, navyHex, mutedHex, textHex)
                        );
                        sigTable.Append(sigRow);
                        body.AppendChild(sigTable);

                        // Thiết lập khổ giấy A4 & lề cân đối
                        body.AppendChild(new SectionProperties(
                            new PageSize { Width = 11906, Height = 16838 },
                            new PageMargin { Top = 850, Bottom = 850, Left = 960, Right = 960 }
                        ));
                    }

                    memoryStream.Seek(0, SeekOrigin.Begin);
                    return File(memoryStream.ToArray(), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"PhieuMuon_{id}.docx");
                }
                else
                {
                    return BadRequest("Định dạng không hợp lệ. Chỉ hỗ trợ PDF hoặc Word.");
                }
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
                    new FontSize { Val = "21" },
                    new Bold(),
                    new Color { Val = labelColorHex }
                ),
                new DocumentFormat.OpenXml.Wordprocessing.Text(label + ": ") { Space = SpaceProcessingModeValues.Preserve }
            );

            var valueProps = new RunProperties(
                new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
                new FontSize { Val = "21" },
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