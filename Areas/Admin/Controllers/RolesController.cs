using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class RolesController : Controller
    {
        private readonly DataContext _context;

        public RolesController(DataContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            var hslist = _context.tblRoles.OrderBy(h => h.RoleID).ToList();

            return View(hslist);
        }

        public IActionResult Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var hs = _context.tblRoles.Find(id);

            if (hs == null)
                return NotFound();

            if (hs.RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Không được xóa vai trò Admin.";
                return RedirectToAction(nameof(Index));
            }

            return View(hs);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            var delhs = _context.tblRoles.Find(id);

            if (delhs == null)
                return NotFound();

            if (delhs.RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Không được xóa vai trò Admin.";
                return RedirectToAction(nameof(Index));
            }

            _context.tblRoles.Remove(delhs);
            _context.SaveChanges();

            return RedirectToAction(nameof(Index));
        }

        public IActionResult Create()
        {
            var mnList = (from m in _context.tblRoles
                          select new SelectListItem()
                          {
                              Text = (m.RoleID == 1) ? m.RoleName : "-- " + m.RoleName,
                              Value = m.RoleID.ToString()
                          }).ToList();
            mnList.Insert(0, new SelectListItem()
            {
                Text = "--- select ---",
                Value = "0"
            });
            ViewBag.mnList = mnList;

            return View();
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(tblRoles mn)
        {
            mn.RoleName = mn.RoleName?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(mn.RoleName))
            {
                ModelState.AddModelError(
                    nameof(mn.RoleName),
                    "Vui lòng nhập tên vai trò.");
            }

            if (ModelState.IsValid)
            {
                bool isDuplicate = _context.tblRoles
                    .AsEnumerable()
                    .Any(r =>
                        string.Equals(
                            r.RoleName?.Trim(),
                            mn.RoleName,
                            StringComparison.OrdinalIgnoreCase));

                if (isDuplicate)
                {
                    ModelState.AddModelError(
                        nameof(mn.RoleName),
                        "Tên vai trò đã tồn tại, không phân biệt chữ hoa/chữ thường. Vui lòng nhập tên khác.");
                }
            }

            if (!ModelState.IsValid)
            {
                return View(mn);
            }

            _context.tblRoles.Add(mn);
            _context.SaveChanges();

            return RedirectToAction(nameof(Index));
        }


        public IActionResult Edit(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var dp = _context.tblRoles.Find(id);
            if (dp == null)
                return NotFound();

            if (dp.RoleName.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Không được sửa vai trò Admin.";
                return RedirectToAction(nameof(Index));
            }

            var dpList = (from c in _context.tblRoles
                          select new SelectListItem()
                          {
                              Text = (c.RoleID == 1) ? c.RoleName : "-- " + c.RoleName,
                              Value = c.RoleID.ToString()
                          }).ToList();
            dpList.Insert(0, new SelectListItem()
            {
                Text = "--- select ---",
                Value = "0"
            });
            ViewBag.dpList = dpList;

            return View(dp);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(tblRoles dp)
        {
            if (dp == null || dp.RoleID <= 0)
                return NotFound();

            var oldRole = _context.tblRoles.Find(dp.RoleID);

            if (oldRole == null)
                return NotFound();

            // Không cho phép sửa vai trò Admin.
            if (oldRole.RoleName.Equals(
                "Admin",
                StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Không được sửa vai trò Admin.";
                return RedirectToAction(nameof(Index));
            }

            // Chuẩn hóa tên trước khi kiểm tra.
            dp.RoleName = dp.RoleName?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(dp.RoleName))
            {
                ModelState.AddModelError(
                    nameof(dp.RoleName),
                    "Vui lòng nhập tên vai trò.");
            }

            if (ModelState.IsValid)
            {
                // So sánh không phân biệt chữ hoa/chữ thường.
                bool isDuplicate = _context.tblRoles
                    .AsEnumerable()
                    .Any(r =>
                        r.RoleID != dp.RoleID &&
                        string.Equals(
                            r.RoleName?.Trim(),
                            dp.RoleName,
                            StringComparison.OrdinalIgnoreCase));

                if (isDuplicate)
                {
                    ModelState.AddModelError(
                        nameof(dp.RoleName),
                        "Tên vai trò đã tồn tại, không phân biệt chữ hoa/chữ thường. Vui lòng nhập tên khác.");
                }
            }

            if (!ModelState.IsValid)
            {
                return View(dp);
            }

            // Cập nhật entity đang được EF Core theo dõi.
            oldRole.RoleName = dp.RoleName;

            _context.SaveChanges();

            return RedirectToAction(nameof(Index));
        }

    }
}