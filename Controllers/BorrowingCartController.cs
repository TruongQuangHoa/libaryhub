// LibraryHub.Controllers/BorrowingCartController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LibraryHub.Models;
using LibraryHub.Utilities;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using System.Text.Json;

namespace LibraryHub.Controllers
{
    // Chỉ cho phép người dùng có Role là "Patron" truy cập vào các tính năng mượn sách
    [Authorize(Roles = "Patron")]
    public class BorrowingCartController : Controller
    {
        private readonly DataContext _context;

        public BorrowingCartController(DataContext context)
        {
            _context = context;
        }

        // Lấy giỏ mượn từ session
        private List<BorrowingCartItem> GetCart()
        {
            var cartJson = HttpContext.Session.GetString("BorrowingCart");
            if (string.IsNullOrEmpty(cartJson))
                return new List<BorrowingCartItem>();
            return JsonSerializer.Deserialize<List<BorrowingCartItem>>(cartJson) ?? new List<BorrowingCartItem>();
        }

        // Lưu giỏ mượn vào session
        private void SaveCart(List<BorrowingCartItem> cart)
        {
            HttpContext.Session.SetString("BorrowingCart", JsonSerializer.Serialize(cart));
        }

        // LibraryHub.Controllers/BorrowingCartController.cs

        [HttpPost]
        [Authorize] // Bắt buộc đăng nhập. Nếu chưa đăng nhập, ASP.NET Core sẽ tự redirect về trang Login.
        [ValidateAntiForgeryToken]
        public IActionResult AddToCart(int bookId, string returnUrl)
        {
            // 1. Kiểm tra sách có tồn tại và còn trong kho không
            var book = _context.Books
                .FirstOrDefault(b => b.BookID == bookId && b.IsActive);

            if (book == null || (book.BookQuantity - book.BorrowQuantity) <= 0)
            {
                TempData["ErrorMessage"] = "Sách đã hết hoặc không tồn tại.";
                return Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
            }

            // 2. Lấy giỏ hàng hiện tại từ Session
            var cart = GetCart();

            // 3. Kiểm tra xem sách đã có trong giỏ chưa (tránh mượn trùng)
            var existingItem = cart.FirstOrDefault(i => i.BookID == bookId);
            if (existingItem != null)
            {
                TempData["ErrorMessage"] = "Bạn đã thêm sách này vào phiếu mượn rồi.";
                return Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
            }

            // 4. Thêm sách mới vào giỏ
            cart.Add(new BorrowingCartItem
            {
                BookID = book.BookID,
                Title = book.TitleBook,
                ImageUrl = book.ImageBook ?? "/images/no-image.png",
                BorrowingFee = 10000m, // Phí cố định
                Quantity = 1
            });

            // 5. Lưu lại vào Session
            SaveCart(cart);

            TempData["SuccessMessage"] = "Đã thêm vào phiếu mượn thành công!";

            // 6. Quay lại trang trước đó
            return Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
        }
        // Xem phiếu mượn (giỏ hàng)
        public IActionResult Cart()
        {
            var cart = GetCart();
            ViewBag.TotalFee = cart.Sum(i => i.BorrowingFee * i.Quantity);
            return View(cart);
        }

        // Xóa một sách khỏi giỏ
        public IActionResult Remove(int bookId)
        {
            var cart = GetCart();
            var item = cart.FirstOrDefault(i => i.BookID == bookId);
            if (item != null)
            {
                cart.Remove(item);
                SaveCart(cart);
            }
            return RedirectToAction("Cart");
        }

        // Xóa toàn bộ giỏ
        public IActionResult Clear()
        {
            HttpContext.Session.Remove("BorrowingCart");
            return RedirectToAction("Cart");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmBorrowing(DateTime borrowDate, DateTime dueDate)
        {
            // 1. Lấy giỏ hàng từ Session
            var cart = GetCart();
            if (cart == null || !cart.Any())
            {
                TempData["ErrorMessage"] = "Giỏ mượn của bạn đang trống!";
                return RedirectToAction("Cart");
            }

            // 2. Xác thực thông tin Độc giả (Patron)
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdClaim)) return RedirectToAction("Index", "Login");

            int currentUserId = int.Parse(userIdClaim);
            var patron = await _context.Patrons.FirstOrDefaultAsync(p => p.UserID == currentUserId && p.IsActive);

            if (patron == null)
            {
                TempData["ErrorMessage"] = "Tài khoản của bạn chưa được cấp quyền độc giả hoặc bị khóa.";
                return RedirectToAction("Cart");
            }

            // 3. Kiểm tra tính hợp lệ của thời gian mượn (Ghi nhận chính xác đến từng phút)
            // Cập nhật lại borrowDate thành giờ thực tế lúc bấm nút nếu cần, hoặc dùng giá trị từ Form
            DateTime now = DateTime.Now;
            if (dueDate <= now)
            {
                TempData["ErrorMessage"] = "Thời gian trả dự kiến phải sau thời gian hiện tại.";
                return RedirectToAction("Cart");
            }

            if (dueDate > now.AddDays(14))
            {
                TempData["ErrorMessage"] = "Thời gian mượn sách tối đa không quá 14 ngày.";
                return RedirectToAction("Cart");
            }

            // 4. Lấy thủ thư hệ thống cố định: Librarian Online
            var onlineLibrarian = await _context.Librarians
                .FirstOrDefaultAsync(l =>
                    l.FullName == "Librarian Online" &&
                    l.IsActive);

            if (onlineLibrarian == null)
            {
                TempData["ErrorMessage"] =
                    "Không tìm thấy thủ thư hệ thống Librarian Online. Vui lòng liên hệ quản trị viên.";

                return RedirectToAction("Cart");
            }

            int librarianId = onlineLibrarian.LibrarianID;

            // 5. Bắt đầu Transaction
            using (var transaction = await _context.Database.BeginTransactionAsync())
            {
                try
                {
                    // Tạo phiếu mượn chính
                    var borrowing = new tblBorrowing
                    {
                        PatronID = patron.PatronID,
                        BorrowDate = now, // Ghi nhận chính xác thời điểm bấm xác nhận
                        DueDate = dueDate, // Thời điểm người dùng chọn trên lịch (có cả giờ)
                        LibrarianID = librarianId,
                        TotalBorrowingFee = cart.Sum(i => i.BorrowingFee * i.Quantity),
                        IsActive = true,
                        CreateDate = now
                    };

                    _context.Borrowings.Add(borrowing);
                    await _context.SaveChangesAsync();

                    // 6. Xử lý chi tiết và cập nhật kho
                    foreach (var item in cart)
                    {
                        var book = await _context.Books.FindAsync(item.BookID);
                        if (book == null || (book.BookQuantity - book.BorrowQuantity) < item.Quantity)
                        {
                            throw new Exception($"Sách '{item.Title}' hiện đã hết.");
                        }

                        _context.BorrowingDetails.Add(new tblBorrowingDetail
                        {
                            BorrowingID = borrowing.BorrowingID,
                            BookID = item.BookID,
                            BorrowingFee = item.BorrowingFee,
                            IsActive = true
                        });

                        book.BorrowQuantity += item.Quantity;
                        book.TotalReaders += 1;
                        _context.Books.Update(book);
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    HttpContext.Session.Remove("BorrowingCart");
                    TempData["SuccessMessage"] = "Mượn sách thành công! Vui lòng trả sách trước " + dueDate.ToString("HH:mm dd/MM/yyyy");

                    return RedirectToAction("Cart");
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    TempData["ErrorMessage"] = "Lỗi: " + ex.Message;
                    return RedirectToAction("Cart");
                }
            }
        }
    }
}