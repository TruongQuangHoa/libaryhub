
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using LibraryHub.Areas.Admin.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Areas.Admin.Components
{
    [ViewComponent(Name = "AdminMenu")]
    public class AdminMenuComponent : ViewComponent
    {
        private readonly DataContext _context;

        public AdminMenuComponent(DataContext context)
        {
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var allMenus = await _context.AdminMenus
    .Where(m => m.IsActive == true)
    .ToListAsync();

            var user = HttpContext.User;

            // Admin được xem toàn bộ menu
            if (user.IsInRole("Admin"))
            {
                return View("Default", allMenus);
            }

            // Các Controller mà Librarian được phép sử dụng
            var allowedControllers = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "Category",
                "Book",
                "Borrowing",
                "BorrowingDetail",
                "Return",
                "ReturnDetail"
            };

            // Chỉ lấy các menu nghiệp vụ được phép
            var allowedMenus = allMenus
                .Where(m =>
                    !string.IsNullOrWhiteSpace(m.ControllerName) &&
                    allowedControllers.Contains(
                        NormalizeControllerName(m.ControllerName)))
                .ToList();

            // Danh sách ID menu được phép hiển thị.
            // AdminMenuID là bigint nên dùng long.
            var visibleMenuIds = new HashSet<long>(
                allowedMenus.Select(m => m.AdminMenuID));

            // Tra cứu menu theo ID.
            var menuLookup = allMenus.ToDictionary(
                m => m.AdminMenuID);

            // Giữ lại các menu cha để bảo toàn cấu trúc menu nhiều cấp.
            foreach (var menu in allowedMenus)
            {
                // ParentLevel là int? hoặc int, tùy model.
                var parentId = menu.ParentLevel;

                while (parentId > 0 &&
                       menuLookup.TryGetValue((long)parentId, out var parent))
                {
                    if (!visibleMenuIds.Add(parent.AdminMenuID))
                    {
                        break;
                    }

                    parentId = parent.ParentLevel;
                }
            }

            // Lọc các menu cần hiển thị.
            var librarianMenus = allMenus
                .Where(m => visibleMenuIds.Contains(m.AdminMenuID))
                .ToList();

            // Không có quyền Admin hoặc Librarian thì không hiển thị menu
            if (!user.IsInRole("Librarian"))
            {
                librarianMenus.Clear();
            }

            return View("Default", librarianMenus);
        }

        private static string NormalizeControllerName(string name)
        {
            name = name.Trim();

            return name.EndsWith(
                "Controller",
                StringComparison.OrdinalIgnoreCase)
                ? name[..^"Controller".Length]
                : name;
        }
    }
}
