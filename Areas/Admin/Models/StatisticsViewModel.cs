using System;
using System.Collections.Generic;
using static LibraryHub.Services.LibraryStatisticsService;

namespace LibraryHub.Areas.Admin.Models
{
    public class StatisticsViewModel
    {
        // 1. Thông tin tổng quan
        public GeneralSummary? Summary { get; set; }

        // 2. Sách được mượn nhiều nhất
        public List<BookBorrowingCount>? TopBooks { get; set; }

        // 3. Thống kê theo thể loại
        public List<BooksByCategory>? BooksByCategory { get; set; }

        // 4. Thống kê mượn sách theo tháng
        public List<MonthlyBorrowing>? MonthlyBorrowing { get; set; }

        // 5. Thống kê doanh thu và lượt mượn
        public StatisticsResult? RevenueStatistics { get; set; }

        // 6. Loại thống kê: day, month, year
        public string StatisticsType { get; set; } = "month";

        // 7. Khoảng thời gian thống kê
        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }
    }
}