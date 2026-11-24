using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Services
{
    public class LibraryStatisticsService
    {
        private readonly DataContext _context;

        public LibraryStatisticsService(DataContext context)
        {
            _context = context;
        }

        // Các class model dùng cho thống kê
        public class GeneralSummary
        {
            public int TotalBooks { get; set; }
            public int TotalActivePatrons { get; set; }
            public int TotalBorrowingRecords { get; set; }
            public int TotalCategories { get; set; }
        }

        public class BookBorrowingCount
        {
            public string TitleBook { get; set; } = string.Empty;
            public string? Author { get; set; }
            public int BorrowQuantity { get; set; }
        }

        public class BooksByCategory
        {
            public string CategoryName { get; set; } = string.Empty;
            public int BookCount { get; set; } // Số đầu sách
            public int TotalQuantity { get; set; } // Tổng số bản sách
        }

        public class MonthlyBorrowing
        {
            public int Month { get; set; }
            public int TotalBorrowings { get; set; }
        }

        // Logic lấy dữ liệu tổng hợp
        public async Task<GeneralSummary> GetGeneralSummaryAsync()
        {
            return new GeneralSummary
            {
                TotalBooks = await _context.Books.CountAsync(),
                TotalActivePatrons = await _context.Patrons.CountAsync(p => p.IsActive),
                TotalBorrowingRecords = await _context.Borrowings.CountAsync(),
                TotalCategories = await _context.Categories.CountAsync()
            };
        }

        public async Task<List<BookBorrowingCount>> GetTopBooksAsync(int count = 5)
        {
            return await _context.Books
                .OrderByDescending(b => b.BorrowQuantity)
                .Take(count)
                .Select(b => new BookBorrowingCount
                {
                    TitleBook = b.TitleBook,
                    Author = b.Author,
                    BorrowQuantity = b.BorrowQuantity
                }).ToListAsync();
        }

        public async Task<List<BooksByCategory>> GetBooksByCategoryAsync()
        {
            return await _context.Categories
                .Select(c => new BooksByCategory
                {
                    CategoryName = c.CategoryName,
                    BookCount = c.book.Count,
                    TotalQuantity = c.book.Sum(b => b.BookQuantity)
                }).ToListAsync();
        }

        public async Task<List<MonthlyBorrowing>> GetMonthlyBorrowingAsync(int year)
        {
            var data = await _context.Borrowings
                .Where(b => b.BorrowDate.HasValue && b.BorrowDate.Value.Year == year)
                .GroupBy(b => b.BorrowDate!.Value.Month)
                .Select(g => new MonthlyBorrowing
                {
                    Month = g.Key,
                    TotalBorrowings = g.Count()
                }).ToListAsync();

            // Đảm bảo đủ 12 tháng (điền 0 vào tháng không có dữ liệu)
            for (int i = 1; i <= 12; i++)
            {
                if (!data.Any(d => d.Month == i))
                    data.Add(new MonthlyBorrowing { Month = i, TotalBorrowings = 0 });
            }

            return data.OrderBy(d => d.Month).ToList();
        }

        public class RevenueBorrowingStatistics
        {
            public DateTime Date { get; set; }

            public int TotalBorrowings { get; set; }

            public decimal BorrowingRevenue { get; set; }

            public decimal FineRevenue { get; set; }

            public decimal TotalRevenue => BorrowingRevenue + FineRevenue;
        }

        public class StatisticsResult
        {
            public int TotalBorrowings { get; set; }

            public decimal TotalBorrowingRevenue { get; set; }

            public decimal TotalFineRevenue { get; set; }

            public decimal TotalRevenue => TotalBorrowingRevenue + TotalFineRevenue;

            public List<RevenueBorrowingStatistics> Details { get; set; }
                = new List<RevenueBorrowingStatistics>();
        }

        public async Task<StatisticsResult> GetRevenueBorrowingStatisticsAsync(
    DateTime startDate,
    DateTime endDate,
    string type)
        {
            startDate = startDate.Date;
            endDate = endDate.Date;

            if (endDate < startDate)
                throw new ArgumentException("Ngày kết thúc không được nhỏ hơn ngày bắt đầu.");

            string statisticsType = (type ?? "day").ToLower();

            if (statisticsType != "day" &&
                statisticsType != "month" &&
                statisticsType != "year")
            {
                statisticsType = "day";
            }

            DateTime endExclusive = endDate.AddDays(1);

            // Lấy phiếu mượn trong khoảng thời gian
            var borrowings = await _context.Borrowings
                .Where(b => b.BorrowDate.HasValue
                            && b.BorrowDate.Value >= startDate
                            && b.BorrowDate.Value < endExclusive)
                .Select(b => new
                {
                    BorrowDate = b.BorrowDate!.Value,
                    b.TotalBorrowingFee
                })
                .ToListAsync();

            // Lấy phiếu trả trong khoảng thời gian
            var returns = await _context.Set<tblReturn>()
                .Where(r => r.ReturnDate.HasValue
                            && r.ReturnDate.Value >= startDate
                            && r.ReturnDate.Value < endExclusive)
                .Select(r => new
                {
                    ReturnDate = r.ReturnDate!.Value,
                    r.TotalFine
                })
                .ToListAsync();

            // Xác định mốc thời gian cần nhóm
            DateTime GetGroupDate(DateTime date)
            {
                switch (statisticsType)
                {
                    case "month":
                        return new DateTime(date.Year, date.Month, 1);

                    case "year":
                        return new DateTime(date.Year, 1, 1);

                    default:
                        return date.Date;
                }
            }

            // Thống kê số lượt mượn và phí mượn
            var borrowingData = borrowings
                .GroupBy(b => GetGroupDate(b.BorrowDate))
                .ToDictionary(
                    g => g.Key,
                    g => new
                    {
                        Count = g.Count(),
                        Revenue = g.Sum(x => x.TotalBorrowingFee)
                    });

            // Thống kê tiền phạt theo ngày trả
            var fineData = returns
                .GroupBy(r => GetGroupDate(r.ReturnDate))
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.TotalFine)
                );

            // Tạo danh sách các mốc thời gian, kể cả mốc không có dữ liệu
            var periods = new List<DateTime>();

            DateTime current = GetGroupDate(startDate);
            DateTime last = GetGroupDate(endDate);

            while (current <= last)
            {
                periods.Add(current);

                current = statisticsType switch
                {
                    "month" => current.AddMonths(1),
                    "year" => current.AddYears(1),
                    _ => current.AddDays(1)
                };
            }

            var result = new StatisticsResult();

            foreach (var date in periods)
            {
                borrowingData.TryGetValue(date, out var borrowing);
                fineData.TryGetValue(date, out var fine);

                result.Details.Add(new RevenueBorrowingStatistics
                {
                    Date = date,
                    TotalBorrowings = borrowing?.Count ?? 0,
                    BorrowingRevenue = borrowing?.Revenue ?? 0,
                    FineRevenue = fine
                });
            }

            result.TotalBorrowings = result.Details.Sum(x => x.TotalBorrowings);
            result.TotalBorrowingRevenue = result.Details.Sum(x => x.BorrowingRevenue);
            result.TotalFineRevenue = result.Details.Sum(x => x.FineRevenue);

            return result;
        }
    }
}