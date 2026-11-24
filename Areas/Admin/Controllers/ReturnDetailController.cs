using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using LibraryHub.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class ReturnDetailController : Controller
    {
        private readonly DataContext _context;
        private readonly ReturnService _returnService;
        private readonly IFeePaymentService _feePaymentService;

        public ReturnDetailController(DataContext context, ReturnService returnService, IFeePaymentService feePaymentService)
        {
            _context = context;
            _returnService = returnService;
            _feePaymentService = feePaymentService;
        }

        public async Task<IActionResult> Index(int? returnId) // ← Thay int thành int?
        {
            IQueryable<tblReturnDetail> query = _context.ReturnDetails
                .Include(rd => rd._return)
                    .ThenInclude(r => r!.borrowing)
                        .ThenInclude(b => b!.patron)
                .Include(rd => rd._return)
                    .ThenInclude(r => r!.librarian)
                .Include(rd => rd.book)
                    .ThenInclude(b => b!.category);

            if (returnId.HasValue && returnId.Value > 0)
            {
                query = query.Where(rd => rd.ReturnID == returnId.Value);
                ViewBag.CurrentReturnId = returnId.Value;
                ViewBag.Title = $"Chi tiết phiếu trả #{returnId.Value}";
            }
            else
            {
                ViewBag.Title = "Tất cả chi tiết phiếu trả";
            }

            var list = await query
                .OrderBy(rd => rd.ReturnID)
                .ToListAsync();

            return View(list);
        }

        public IActionResult Create()
        {
            var returnList = _context.Returns
                .Where(r => r.IsActive == false) // Chỉ lấy các phiếu đang hoạt động/chưa đóng
                .Include(r => r.borrowing)
                    .ThenInclude(b => b!.patron)
                .AsEnumerable()
                .Select(r => new
                {
                    r.ReturnID,
                    Display = $"Phiếu trả {r.ReturnID} - (Mượn gốc: {r.BorrowingID} - {r.borrowing?.patron?.PatronName ?? "N/A"})"
                })
                .ToList();

            ViewBag.ReturnID = new SelectList(returnList, "ReturnID", "Display");
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetBooksByReturn(int returnId)
        {
            var returnRecord = await _context.Returns
                .Include(r => r.borrowing!)
                .FirstOrDefaultAsync(r => r.ReturnID == returnId);

            if (returnRecord == null || returnRecord.borrowing == null)
                return NotFound();

            DateTime returnDate = returnRecord.ReturnDate ?? DateTime.Now;
            DateTime dueDate = returnRecord.borrowing.DueDate ?? returnDate;

            int overdueDays = await _feePaymentService.CalculateOverdueDaysAsync(dueDate, returnDate);
            decimal overdueFine = await _feePaymentService.CalculateOverdueFineAsync(dueDate, returnDate);

            var existingDetails = await _context.ReturnDetails
                .Where(rd => rd.ReturnID == returnId && rd.IsActive)
                .Include(rd => rd.book)
                .Select(rd => new
                {
                    bookID = rd.BookID,
                    titleBook = rd.book!.TitleBook,
                    overdueDays = rd.OverdueDays,
                    overdueFine = rd.OverdueFine,
                    lostFine = rd.LostFine,
                    isDamaged = rd.IsDamaged
                })
                .ToListAsync();

            if (existingDetails.Any())
            {
                return Json(existingDetails);
            }

            var books = await _context.BorrowingDetails
                .Where(bd => bd.BorrowingID == returnRecord.BorrowingID && bd.IsActive)
                .Include(bd => bd.book)
                .Select(bd => new
                {
                    bookID = bd.BookID,
                    titleBook = bd.book!.TitleBook,
                    overdueDays = overdueDays,
                    overdueFine = overdueFine,
                    lostFine = 0m,
                    isDamaged = false
                })
                .ToListAsync();

            return Json(books);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(int ReturnID, List<tblReturnDetail> details)
        {
            if (ReturnID <= 0 || details == null || !details.Any())
            {
                ModelState.AddModelError("", "Vui lòng chọn phiếu trả và nhập chi tiết sách.");
                LoadReturnDropdown();
                return View();
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var returnRecord = await _context.Returns
                    .Include(r => r.borrowing!)
                        .ThenInclude(b => b!.borrowingDetail!)
                    .FirstOrDefaultAsync(r => r.ReturnID == ReturnID);

                if (returnRecord == null || returnRecord.borrowing == null)
                {
                    await transaction.RollbackAsync();
                    ModelState.AddModelError("", "Không tìm thấy phiếu trả hoặc phiếu mượn gốc.");
                    LoadReturnDropdown();
                    return View();
                }

                int borrowingId = returnRecord.borrowing.BorrowingID;

                // Tính phí quá hạn chung (dựa trên ReturnDate và DueDate) theo mức phạt trong CSDL
                DateTime returnDate = returnRecord.ReturnDate ?? DateTime.Now;
                DateTime dueDate = returnRecord.borrowing.DueDate ?? returnDate;
                int overdueDays = await _feePaymentService.CalculateOverdueDaysAsync(dueDate, returnDate);
                decimal overdueFinePerBook = await _feePaymentService.CalculateOverdueFineAsync(dueDate, returnDate);

                // Tổng số sách đang mượn (IsActive = true) trước khi xử lý
                int activeBorrowedBooksCount = returnRecord.borrowing.borrowingDetail!
                    .Count(bd => bd.IsActive);

                foreach (var item in details)
                {
                    item.ReturnID = ReturnID;
                    item.IsActive = true;

                    // TỰ ĐỘNG GÁN phí quá hạn nếu chưa có
                    if (item.OverdueDays == 0)
                    {
                        item.OverdueDays = overdueDays;
                        item.OverdueFine = overdueFinePerBook;
                    }

                    var existing = await _context.ReturnDetails
                        .FirstOrDefaultAsync(rd => rd.ReturnID == ReturnID && rd.BookID == item.BookID);

                    if (existing != null)
                    {
                        existing.IsDamaged = item.IsDamaged;
                        existing.LostFine = item.LostFine;
                        existing.OverdueDays = item.OverdueDays;
                        existing.OverdueFine = item.OverdueFine;
                    }
                    else
                    {
                        _context.ReturnDetails.Add(item);
                    }

                    // Cập nhật sách
                    var book = await _context.Books.FindAsync(item.BookID);
                    if (book != null)
                    {
                        book.BorrowQuantity = Math.Max(0, book.BorrowQuantity - 1);
                    }

                    // Vô hiệu hóa chi tiết mượn
                    var borrowDetail = await _context.BorrowingDetails
                        .FirstOrDefaultAsync(bd => bd.BorrowingID == borrowingId
                            && bd.BookID == item.BookID
                            && bd.IsActive);

                    if (borrowDetail != null)
                    {
                        borrowDetail.IsActive = false;
                    }
                }

                await _context.SaveChangesAsync();

                // Tính lại tổng phạt
                var allReturnDetails = await _context.ReturnDetails
                    .Where(rd => rd.ReturnID == ReturnID && rd.IsActive)
                    .ToListAsync();


                await _returnService.UpdateTotalFineAndStatus(ReturnID);
                await transaction.CommitAsync();


                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                ModelState.AddModelError("", "Lỗi: " + ex.Message);
                LoadReturnDropdown();
                return View();
            }
        }

        // GET: Admin/ReturnDetail/Edit/115
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var detail = await _context.ReturnDetails
                .Include(rd => rd._return!)
                    .ThenInclude(r => r!.borrowing!)
                        .ThenInclude(b => b!.patron)
                .Include(rd => rd.book)
                .FirstOrDefaultAsync(rd => rd.ReturnDetailID == id && rd.IsActive);

            if (detail == null)
            {
                return NotFound();
            }

            return View(detail); // Trả về View "Edit.cshtml" với model là tblReturnDetail đơn lẻ
        }

        // POST: Edit
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int ReturnID, List<tblReturnDetail> details)
        {
            if (details == null || !details.Any() || details[0].ReturnDetailID <= 0)
            {
                ModelState.AddModelError("", "Dữ liệu không hợp lệ.");
                return View(details[0]);
            }

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var item = details[0];
                var existingDetail = await _context.ReturnDetails
                    .FirstOrDefaultAsync(rd => rd.ReturnDetailID == item.ReturnDetailID && rd.IsActive);

                if (existingDetail == null)
                {
                    ModelState.AddModelError("", "Không tìm thấy chi tiết trả sách.");
                    return View(item);
                }

                // Chỉ cập nhật 2 trường được phép sửa
                existingDetail.IsDamaged = item.IsDamaged;
                existingDetail.LostFine = item.IsDamaged ? item.LostFine : 0m;

                _context.ReturnDetails.Update(existingDetail);
                await _context.SaveChangesAsync();

                // Cập nhật lại tổng phạt cho phiếu trả
                await _returnService.UpdateTotalFineAndStatus(ReturnID);

                await transaction.CommitAsync();

                TempData["Success"] = "Cập nhật chi tiết trả sách thành công!";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                ModelState.AddModelError("", "Lỗi khi cập nhật: " + ex.Message);
                return View(details[0]);
            }
        }

        // Hàm load dropdown khi lỗi (giữ nguyên hoặc thêm vào controller)
        private void LoadReturnDropdown()
        {
            var returnList = _context.Returns
                .Where(r => r.IsActive == false) // Chỉ lấy các phiếu đang hoạt động/chưa đóng
                .Include(r => r.borrowing)
                    .ThenInclude(b => b!.patron)
                .AsEnumerable()
                .Select(r => new
                {
                    r.ReturnID,
                    Display = $"Phiếu trả {r.ReturnID} - (Mượn gốc: {r.BorrowingID} - {r.borrowing?.patron?.PatronName ?? "N/A"})"
                })
                .ToList();

            ViewBag.ReturnID = new SelectList(returnList, "ReturnID", "Display");
        }

    }
}