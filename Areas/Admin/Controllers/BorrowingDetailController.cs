using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using LibraryHub.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class BorrowingDetailController : Controller
    {
        private readonly DataContext _context;

        public BorrowingDetailController(DataContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            var list = _context.BorrowingDetails
                        .Include(h => h.borrowing)
                        .ThenInclude(h => h.patron)
                        .Include(h => h.book)
                        .ThenInclude(h => h.category)
                        .ToList();
            return View(list);
        }

        private void LoadData(int? selectedBookID = null, int? selectedBorrowingID = null, int? currentBookID = null)
        {
            // 1. Danh sách phiếu mượn
            var BorrowingQuery = _context.Borrowings
                .Include(b => b.patron)
                .Where(b => b.IsActive == true
                        && b.DueDate.HasValue
                        && b.DueDate.Value >= DateTime.Now)
                .ToList();

            var BorrowingListItems = BorrowingQuery
                .Select(b => new SelectListItem
                {
                    Value = b.BorrowingID.ToString(),
                    Text = b.BorrowingID + " - " + (b.patron != null ? b.patron.PatronName : "Độc giả không xác định"),
                    Selected = b.BorrowingID == selectedBorrowingID
                })
                .ToList();

            ViewBag.BorrowingList = BorrowingListItems;

            // 2. Danh sách sách: Hiển thị sách còn hàng, HOẶC là sách đang được chọn, HOẶC là sách ban đầu của chi tiết phiếu mượn này
            var BookQuery = _context.Books
                .Include(l => l.category)
                .Where(l => l.IsActive)
                .AsQueryable();

            var availableBooks = BookQuery
                .Where(l => (l.BookQuantity - l.BorrowQuantity > 0)
                            || l.BookID == selectedBookID
                            || l.BookID == currentBookID);

            var bookListItems = availableBooks
                .Select(l => new SelectListItem
                {
                    Value = l.BookID.ToString(),
                    Text = l.TitleBook
                        + " | Thể loại: " + (l.category != null ? l.category.CategoryName : "N/A"),
                    Selected = l.BookID == selectedBookID
                })
                .OrderBy(l => l.Text)
                .ToList();

            ViewBag.bookList = bookListItems;
        }

        public IActionResult Create()
        {
            LoadData();
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(tblBorrowingDetail model)
        {
            if (!ModelState.IsValid)
            {
                LoadData(model.BookID, model.BorrowingID);
                return View(model);
            }

            // Lấy thông tin phiếu mượn để biết PatronID
            var borrowing = await _context.Borrowings
                .Include(b => b.patron)
                .FirstOrDefaultAsync(b => b.BorrowingID == model.BorrowingID);



            if (borrowing == null || borrowing.patron == null)
            {
                ViewBag.BorrowingError = "Không tìm thấy phiếu mượn hoặc độc giả.";
                LoadData(model.BookID, model.BorrowingID);
                return View(model);
            }

            // Không cho phép thêm sách vào phiếu đã quá hạn
            if (borrowing.IsActive &&
    borrowing.DueDate.HasValue &&
    borrowing.DueDate.Value < DateTime.Now)
            {
                ViewBag.BorrowingError =
                    $"Phiếu mượn #{borrowing.BorrowingID} đã quá hạn vào " +
                    $"{borrowing.DueDate.Value:dd/MM/yyyy HH:mm}. " +
                    "Không thể thêm sách mới vào phiếu này.";

                LoadData(model.BookID, null);
                return View(model);
            }

            int patronId = borrowing.patron.PatronID;

            // === KIỂM TRA GIỚI HẠN 5 QUYỂN ===
            int currentActiveBooksCount = await _context.BorrowingDetails
                .CountAsync(bd => bd.BorrowingID == model.BorrowingID && bd.IsActive);

            if (currentActiveBooksCount >= 5)
            {
                ViewBag.BorrowingError =
                    "Phiếu mượn này đã có 5 quyển sách. " +
                    "Không thể thêm sách nữa. Giới hạn tối đa là 5 quyển/phiếu.";

                LoadData(model.BookID, model.BorrowingID);
                return View(model);
            }

            // Kiểm tra sách tồn tại và còn hàng
            var book = await _context.Books.FirstOrDefaultAsync(l => l.BookID == model.BookID && l.IsActive);
            if (book == null)
            {
                ViewBag.BookError = "Sách không tồn tại hoặc không hoạt động.";
                LoadData(model.BookID, model.BorrowingID);
                return View(model);
            }

            if (book.BookQuantity - book.BorrowQuantity <= 0)
            {
                ViewBag.BookError = $"Sách \"{book.TitleBook}\" đã hết.";
                LoadData(model.BookID, model.BorrowingID);
                return View(model);
            }

            // Kiểm tra trùng sách trong phiếu hiện tại
            bool isDuplicate = await _context.BorrowingDetails.AnyAsync(bd =>
    bd.BookID == model.BookID &&
    bd.BorrowingID == model.BorrowingID &&
    bd.IsActive);

            if (isDuplicate)
            {
                ViewBag.BookError =
                    $"Sách \"{book.TitleBook}\" đã có trong phiếu mượn này.";

                LoadData(model.BookID, model.BorrowingID);
                return View(model);
            }

            // === CẬP NHẬT TotalReaders: Chỉ tăng nếu độc giả CHƯA TỪNG mượn sách này ===
            bool hasBorrowedBefore = await _context.BorrowingDetails
                .AnyAsync(bd => bd.BookID == model.BookID
                 && bd.borrowing.PatronID == patronId
                 && bd.IsActive);

            if (!hasBorrowedBefore)
            {
                book.TotalReaders += 1;
                _context.Books.Update(book);
            }

            // === THÊM CHI TIẾT MƯỢN ===
            model.IsActive = true;
            _context.BorrowingDetails.Add(model);

            // Tăng số lượng đang mượn
            book.BorrowQuantity += 1;
            // Không cần Update(book) lần nữa vì đã Update ở trên nếu tăng TotalReaders

            await _context.SaveChangesAsync();

            // Cập nhật tổng phí
            await UpdateTotalBorrowingFeeAsync(model.BorrowingID);

            ViewBag.Success = $"Đã thêm sách '{book.TitleBook}' vào phiếu mượn thành công!" +
     (hasBorrowedBefore
         ? ""
         : $" (Độc giả mới → Tổng độc giả đọc sách này: {book.TotalReaders})");

            LoadData();
            return RedirectToAction(nameof(Index));
        }


        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null || id <= 0)
                return NotFound();

            var detail = await _context.BorrowingDetails
                .Include(d => d.borrowing)
                    .ThenInclude(b => b!.patron)
                .Include(d => d.book)
                    .ThenInclude(b => b!.category)
                .FirstOrDefaultAsync(d => d.BorrowingDetailID == id);

            if (detail == null)
                return NotFound();

            // Truyền thêm detail.BookID làm tham số thứ 3 (currentBookID) để luôn giữ lại sách cũ trong dropdown
            LoadData(detail.BookID, detail.BorrowingID, detail.BookID);

            return View(detail);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(tblBorrowingDetail model)
        {
            // 1. Luôn lấy bản ghi cũ trong DB (có tracking hoặc AsNoTracking) để biết chính xác sách ban đầu là gì
            var oldDetail = await _context.BorrowingDetails
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.BorrowingDetailID == model.BorrowingDetailID);

            if (oldDetail == null)
            {
                ViewBag.Error = "Không tìm thấy chi tiết phiếu mượn.";
                LoadData(model.BookID, model.BorrowingID);
                return View(model);
            }

            // Lưu lại BookID gốc của chi tiết này để luôn hiển thị trong dropdown dù hết hàng
            int originalBookID = oldDetail.BookID;

            // Giữ nguyên mã phiếu mượn trong cơ sở dữ liệu
            model.BorrowingID = oldDetail.BorrowingID;

            // Nạp lại thông tin phiếu mượn và độc giả
            model.borrowing = await _context.Borrowings
                .Include(b => b.patron)
                .FirstOrDefaultAsync(b => b.BorrowingID == oldDetail.BorrowingID);

            // Kiểm tra dữ liệu đầu vào
            if (!ModelState.IsValid)
            {
                LoadData(model.BookID, oldDetail.BorrowingID, originalBookID);
                return View(model);
            }

            // Kiểm tra sách trùng trong cùng phiếu mượn
            if (model.IsActive)
            {
                bool isDuplicate = await _context.BorrowingDetails.AnyAsync(bd =>
                    bd.BookID == model.BookID &&
                    bd.BorrowingID == oldDetail.BorrowingID &&
                    bd.IsActive &&
                    bd.BorrowingDetailID != model.BorrowingDetailID);

                if (isDuplicate)
                {
                    ViewBag.BookError = "Sách này đã có trong phiếu mượn. Vui lòng chọn sách khác.";
                    LoadData(model.BookID, oldDetail.BorrowingID, originalBookID);
                    return View(model);
                }
            }

            bool wasActive = oldDetail.IsActive;
            int oldBookID = oldDetail.BookID;

            // Nếu đổi sách và chi tiết đang được mượn
            if (model.IsActive && oldBookID != model.BookID)
            {
                var newBook = await _context.Books
                    .FirstOrDefaultAsync(b => b.BookID == model.BookID && b.IsActive);

                if (newBook == null)
                {
                    ViewBag.BookError = "Sách không tồn tại hoặc không hoạt động.";
                    LoadData(model.BookID, oldDetail.BorrowingID, originalBookID);
                    return View(model);
                }

                if (newBook.BookQuantity - newBook.BorrowQuantity <= 0)
                {
                    ViewBag.BookError = "Sách mới đã hết, không thể chuyển.";
                    LoadData(model.BookID, oldDetail.BorrowingID, originalBookID);
                    return View(model);
                }
            }

            // Lấy entity đang được EF theo dõi để update
            var detail = await _context.BorrowingDetails
                .FirstOrDefaultAsync(d => d.BorrowingDetailID == model.BorrowingDetailID);

            if (detail == null)
                return NotFound();

            // Chỉ cho phép sửa các trường cần thiết
            detail.BookID = model.BookID;
            detail.BorrowingFee = model.BorrowingFee;
            detail.IsActive = model.IsActive;

            // Điều chỉnh số lượng sách khi đổi trạng thái hoặc đổi sách
            if (wasActive && (!model.IsActive || oldBookID != model.BookID))
            {
                var oldBook = await _context.Books.FindAsync(oldBookID);
                if (oldBook != null)
                    oldBook.BorrowQuantity = Math.Max(0, oldBook.BorrowQuantity - 1);
            }

            if (model.IsActive && (!wasActive || oldBookID != model.BookID))
            {
                var newBook = await _context.Books.FindAsync(model.BookID);
                if (newBook != null)
                    newBook.BorrowQuantity += 1;
            }

            await _context.SaveChangesAsync();

            await UpdateTotalBorrowingFeeAsync(detail.BorrowingID);

            return RedirectToAction(nameof(Index));
        }

        public IActionResult Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            // Nạp chi tiết phiếu mượn cùng với sách và phiếu mượn cha
            var detail = _context.BorrowingDetails
                .Include(d => d.book).ThenInclude(b => b.category)
                .Include(d => d.borrowing).ThenInclude(b => b.patron)
                .FirstOrDefault(d => d.BorrowingDetailID == id);

            if (detail == null)
                return NotFound();

            return View(detail);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var detail = await _context.BorrowingDetails
                .Include(bd => bd.book)
                .FirstOrDefaultAsync(bd => bd.BorrowingDetailID == id);

            if (detail == null)
            {
                TempData["Error"] = "Không tìm thấy chi tiết.";
                return RedirectToAction(nameof(Index));
            }

            int borrowingId = detail.BorrowingID;

            // Nếu đang active → giảm số lượng mượn
            if (detail.IsActive && detail.book != null)
            {
                detail.book.BorrowQuantity = Math.Max(0, detail.book.BorrowQuantity - 1);
                _context.Books.Update(detail.book);
            }

            _context.BorrowingDetails.Remove(detail);
            await _context.SaveChangesAsync();

            // Cập nhật lại tổng phí
            await UpdateTotalBorrowingFeeAsync(borrowingId);

            TempData["Success"] = "Xóa chi tiết thành công.";
            return RedirectToAction(nameof(Index));
        }

        private async Task UpdateTotalBorrowingFeeAsync(int borrowingId)
        {
            var total = await _context.BorrowingDetails
                .Where(bd => bd.BorrowingID == borrowingId && bd.IsActive)
                .SumAsync(bd => (decimal?)bd.BorrowingFee) ?? 0;

            var borrowing = await _context.Borrowings.FirstOrDefaultAsync(b => b.BorrowingID == borrowingId);
            if (borrowing != null)
            {
                borrowing.TotalBorrowingFee = total;
                await _context.SaveChangesAsync(); // QUAN TRỌNG
            }
        }

        [HttpPost]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var detail = await _context.BorrowingDetails
                .Include(bd => bd.book)
                .FirstOrDefaultAsync(bd => bd.BorrowingDetailID == id);

            if (detail == null || detail.book == null)
                return NotFound();

            bool oldStatus = detail.IsActive;
            detail.IsActive = !oldStatus;

            // Cập nhật số lượng mượn
            if (detail.IsActive)
                detail.book.BorrowQuantity += 1;
            else
                detail.book.BorrowQuantity = Math.Max(0, detail.book.BorrowQuantity - 1);

            _context.Update(detail);
            _context.Update(detail.book);
            await _context.SaveChangesAsync();

            // Cập nhật tổng phí
            await UpdateTotalBorrowingFeeAsync(detail.BorrowingID);

            return RedirectToAction(nameof(Index));
        }
    }
}
