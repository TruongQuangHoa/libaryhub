using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LibraryHub.Services
{
    public class ReturnService
    {
        private readonly DataContext _context;

        public ReturnService(DataContext context)
        {
            _context = context;
        }

        public async Task UpdateTotalFineAndStatus(int returnId)
        {
            var returnRecord = await _context.Returns
                .Include(r => r.borrowing)
                .FirstOrDefaultAsync(r => r.ReturnID == returnId);

            if (returnRecord == null) return;

            var details = await _context.ReturnDetails
                .Where(rd => rd.ReturnID == returnId && rd.IsActive)
                .ToListAsync();

            decimal totalFine = details.Sum(d => d.OverdueFine + d.LostFine);

            int returnedCount = details.Count;
            int totalBorrowedCount = await _context.BorrowingDetails
                .CountAsync(bd => bd.BorrowingID == returnRecord.BorrowingID);

            bool isCompleted = returnedCount >= totalBorrowedCount;

            bool hasPaid = false;
            try
            {
                hasPaid = await _context.PaymentHistories
                    .AnyAsync(p => p.ReturnID == returnId &&
                                   (p.PaymentStatus == "Đã thanh toán" || p.PaymentStatus == "Thu dư"));
            }
            catch
            {
                hasPaid = false;
            }

            returnRecord.TotalFine = totalFine;
            // Chỉ đánh dấu phiếu trả "Đã hoàn tất" khi đã xác nhận thanh toán và lưu lịch sử giao dịch
            returnRecord.IsActive = hasPaid;

            // Chỉ chuyển phiếu mượn sang "Đã trả" (IsActive = false) khi phiếu trả đã "Đã hoàn tất" (đã thanh toán)
            if (returnRecord.borrowing != null)
            {
                returnRecord.borrowing.IsActive = !(isCompleted && hasPaid);
            }

            await _context.SaveChangesAsync();
        }
    }
}