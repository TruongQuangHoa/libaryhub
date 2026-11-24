using System;
using System.Threading.Tasks;
using LibraryHub.Areas.Admin.Models;
using LibraryHub.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class StatisticsController : Controller
    {
        private readonly LibraryStatisticsService _statisticsService;

        public StatisticsController(LibraryStatisticsService statisticsService)
        {
            _statisticsService = statisticsService;
        }

        [HttpGet]
        public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate)
        {
            DateTime today = DateTime.Today;

            // Mặc định thống kê tháng hiện tại
            DateTime defaultStartDate = new DateTime(today.Year, today.Month, 1);
            DateTime defaultEndDate = defaultStartDate.AddMonths(1).AddDays(-1);

            // Nếu người dùng không nhập thời gian thì lấy tháng hiện tại
            DateTime fromDate = startDate?.Date ?? defaultStartDate;
            DateTime toDate = endDate?.Date ?? defaultEndDate;

            // Kiểm tra khoảng thời gian
            if (fromDate > toDate)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Ngày bắt đầu không được lớn hơn ngày kết thúc."
                );

                fromDate = defaultStartDate;
                toDate = defaultEndDate;
            }

            int currentYear = today.Year;

            var revenueStatistics = await _statisticsService
                .GetRevenueBorrowingStatisticsAsync(fromDate, toDate, "day");

            var viewModel = new StatisticsViewModel
            {
                Summary = await _statisticsService.GetGeneralSummaryAsync(),

                TopBooks = await _statisticsService.GetTopBooksAsync(5),

                BooksByCategory = await _statisticsService.GetBooksByCategoryAsync(),

                MonthlyBorrowing = await _statisticsService.GetMonthlyBorrowingAsync(currentYear),

                StartDate = fromDate,
                EndDate = toDate,

                StatisticsType = "day",

                RevenueStatistics = revenueStatistics
            };

           return View("~/Areas/Admin/Views/Home/Index.cshtml", viewModel);
        }
    }
}