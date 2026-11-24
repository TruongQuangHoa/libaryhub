using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Controllers
{
    public class BooksController : Controller
{
    private readonly DataContext _context;

    public BooksController(DataContext context) { _context = context; }

    public async Task<IActionResult> Index(int? categoryId)
    {
        // Lấy danh sách danh mục để hiển thị ở sidebar (Ảnh 2 bên trái)
        ViewBag.Categories = await _context.Categories.Where(c => c.IsActive).ToListAsync();
        
        // Truy vấn danh sách sách từ viewBook (hiệu năng tốt hơn)
        var booksQuery = _context.viewBooks.Where(b => b.IsActive);

        // Nếu người dùng chọn một danh mục cụ thể
        if (categoryId.HasValue)
        {
            booksQuery = booksQuery.Where(b => b.CategoryID == categoryId.Value);
            var category = await _context.Categories.FindAsync(categoryId);
            ViewBag.CurrentCategoryName = category?.CategoryName;
        }

        var listBooks = await booksQuery.ToListAsync();
        return View(listBooks);
    }
}
}