
using System;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class PublisherController : Controller
    {
        private readonly DataContext _context;

        public PublisherController(DataContext context)
        {
            _context = context;
        }

        // =========================
        // INDEX
        // =========================
        public async Task<IActionResult> Index()
        {
            var publisherList = await _context.Publishers
                .OrderBy(m => m.PublisherID)
                .ToListAsync();

            return View(publisherList);
        }

        // =========================
        // CREATE - GET
        // =========================
        [HttpGet]
        public IActionResult Create()
        {
            return View(new tblPublisher
            {
                PublisherName = string.Empty,
                IsActive = true,
                CreatedDate = DateTime.Now,
                ModifiedDate = DateTime.Now
            });
        }

        // =========================
        // CREATE - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(tblPublisher publisher)
        {
            if (!ModelState.IsValid)
            {
                return View(publisher);
            }

            // Kiểm tra tên nhà xuất bản đã tồn tại
            bool exists = await _context.Publishers
                .AnyAsync(x => x.PublisherName == publisher.PublisherName);

            if (exists)
            {
                ModelState.AddModelError(
                    "PublisherName",
                    "Tên nhà xuất bản này đã tồn tại."
                );

                return View(publisher);
            }

            // Thiết lập ngày tạo và ngày cập nhật
            publisher.CreatedDate = DateTime.Now;
            publisher.ModifiedDate = DateTime.Now;

            _context.Publishers.Add(publisher);

            await _context.SaveChangesAsync();

            TempData["Success"] = "Thêm nhà xuất bản thành công.";

            return RedirectToAction(nameof(Index));
        }

        // =========================
        // EDIT - GET
        // =========================
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var publisher = await _context.Publishers
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.PublisherID == id);

            if (publisher == null)
                return NotFound();

            return View(publisher);
        }

        // =========================
        // EDIT - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            tblPublisher publisher)
        {
            if (id != publisher.PublisherID)
                return NotFound();

            // 1. Lấy thông tin nhà xuất bản hiện tại
            var existingPublisher = await _context.Publishers
                .FirstOrDefaultAsync(x => x.PublisherID == id);

            if (existingPublisher == null)
                return NotFound();

            // 2. Kiểm tra dữ liệu nhập
            if (!ModelState.IsValid)
            {
                return View(publisher);
            }

            // 3. Kiểm tra tên nhà xuất bản trùng lặp
            bool exists = await _context.Publishers
                .AnyAsync(x =>
                    x.PublisherName == publisher.PublisherName &&
                    x.PublisherID != id);

            if (exists)
            {
                ModelState.AddModelError(
                    "PublisherName",
                    "Tên nhà xuất bản này đã tồn tại."
                );

                return View(publisher);
            }

            // 4. Cập nhật thông tin nhà xuất bản
            existingPublisher.PublisherName = publisher.PublisherName;
            existingPublisher.Description = publisher.Description;
            existingPublisher.Address = publisher.Address;
            existingPublisher.Phone = publisher.Phone;
            existingPublisher.Email = publisher.Email;
            existingPublisher.Website = publisher.Website;
            existingPublisher.IsActive = publisher.IsActive;

            // Giữ nguyên CreatedDate
            // Tự động cập nhật ModifiedDate
            existingPublisher.ModifiedDate = DateTime.Now;

            // 5. Lưu thay đổi
            await _context.SaveChangesAsync();

            TempData["Success"] = "Cập nhật nhà xuất bản thành công.";

            return RedirectToAction(nameof(Index));
        }

        // =========================
        // DELETE - GET
        // =========================
        [HttpGet]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var publisher = await _context.Publishers
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.PublisherID == id);

            if (publisher == null)
                return NotFound();

            return View(publisher);
        }

        // =========================
        // DELETE - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var publisher = await _context.Publishers
                .FirstOrDefaultAsync(x => x.PublisherID == id);

            if (publisher == null)
                return NotFound();

            _context.Publishers.Remove(publisher);

            await _context.SaveChangesAsync();

            TempData["Success"] = "Xóa nhà xuất bản thành công.";

            return RedirectToAction(nameof(Index));
        }

        // =========================
        // TOGGLE STATUS
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var publisher = await _context.Publishers
                .FirstOrDefaultAsync(x => x.PublisherID == id);

            if (publisher == null)
                return NotFound();

            publisher.IsActive = !publisher.IsActive;

            publisher.ModifiedDate = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Cập nhật trạng thái nhà xuất bản thành công.";

            return RedirectToAction(nameof(Index));
        }
    }
}
