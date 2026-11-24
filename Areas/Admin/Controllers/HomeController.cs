using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using LibraryHub.Utilities;
using Microsoft.Extensions.Logging;
using LibraryHub.Services;
using LibraryHub.Areas.Admin.Models;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class HomeController : Controller
    {
        // 1. Khai báo Service
        private readonly LibraryStatisticsService _statisticsService;
        private readonly ILogger<HomeController> _logger;

        // 2. Thêm Service vào Constructor
        public HomeController(LibraryStatisticsService statisticsService, ILogger<HomeController> logger)
        {
            _statisticsService = statisticsService;
            _logger = logger;
        }

        // 3. Thay đổi Index để lấy và truyền Model


        [HttpGet]
        public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate)
        {
            // 1. Kiểm tra đăng nhập
            if (User.Identity?.IsAuthenticated != true)
            {
                return RedirectToAction("Index", "Login", new { area = "" });
            }

            // 2. Kiểm tra quyền truy cập
            if (!User.IsInRole("Admin") && !User.IsInRole("Librarian"))
            {
                return RedirectToAction(
                    nameof(AccessDenied),
                    "Home",
                    new { area = "Admin" }
                );
            }

            try
            {
                // 3. Xác định khoảng thời gian thống kê
                DateTime today = DateTime.Today;

                // Ngày đầu tháng hiện tại
                DateTime defaultStartDate = new DateTime(
                    today.Year,
                    today.Month,
                    1
                );

                // Ngày kết thúc mặc định là ngày hiện tại
                DateTime defaultEndDate = today;

                // Nếu chưa nhập ngày thì mặc định lấy tháng hiện tại
                DateTime fromDate = startDate?.Date ?? defaultStartDate;
                DateTime toDate = endDate?.Date ?? defaultEndDate;

                // Kiểm tra khoảng thời gian hợp lệ
                if (fromDate > toDate)
                {
                    TempData["Error"] =
                        "Ngày bắt đầu không được lớn hơn ngày kết thúc.";

                    fromDate = defaultStartDate;
                    toDate = defaultEndDate;
                }

                // 4. Lấy dữ liệu thống kê tổng quan
                var summary = await _statisticsService
                    .GetGeneralSummaryAsync();

                var topBooks = await _statisticsService
                    .GetTopBooksAsync(5);

                var bookByCategory = await _statisticsService
                    .GetBooksByCategoryAsync();

                var monthlyBorrowing = await _statisticsService
                    .GetMonthlyBorrowingAsync(today.Year);

                // 5. Lấy dữ liệu doanh thu và lượt mượn
                var revenueStatistics = await _statisticsService
                    .GetRevenueBorrowingStatisticsAsync(
                        fromDate,
                        toDate,
                        "day"
                    );

                // 6. Khởi tạo ViewModel
                var viewModel = new StatisticsViewModel
                {
                    Summary = summary,

                    TopBooks = topBooks,

                    BooksByCategory = bookByCategory,

                    MonthlyBorrowing = monthlyBorrowing,

                    // Thông tin khoảng thời gian
                    StartDate = fromDate,
                    EndDate = toDate,

                    // Hình thức thống kê
                    StatisticsType = "day",

                    // Dữ liệu doanh thu và lượt mượn
                    RevenueStatistics = revenueStatistics
                };

                return View(viewModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Lỗi khi tải dữ liệu thống kê thư viện."
                );

                return View(new StatisticsViewModel());
            }
        }

        // ACCESS DENIED
        [HttpGet]
        public IActionResult AccessDenied() { return View(); }

        public IActionResult Logout()
        {
            Functions._UserID = 0;
            Functions._UserName = string.Empty;
            Functions._Email = string.Empty;
            Functions._Message = string.Empty;
            return RedirectToAction("Index", "Home");
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            // Trả về View Error!
            return View("Error!");
        }
    }
}