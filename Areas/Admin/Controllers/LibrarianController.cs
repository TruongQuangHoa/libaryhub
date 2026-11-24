using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using ClosedXML.Excel;
using System.Globalization;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;


namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public class LibrarianController : Controller
    {
        private readonly DataContext _context;
        private readonly IWebHostEnvironment _environment;

        public LibrarianController(
            DataContext context,
            IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        // =========================================================
        // INDEX
        // =========================================================
        public IActionResult Index()
        {
            var lbList = _context.Librarians
                .OrderBy(m => m.LibrarianID)
                .ToList();

            return View(lbList);
        }

        [HttpGet]
        public IActionResult DownloadExcelTemplate()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("ThuThu");

            // Tiêu đề các cột
            string[] headers =
            {
        "FullName",
        "Email",
        "Birth",
        "Gender",
        "Address",
        "StatusLibrarian",
        "CCCD",
        "Nation",
        "Religion",
        "NumberPhone",
        "NumberBHXH",
        "IsActive",
        "Hamlet",
        "Commune",
        "Province",
        "Nationality"
    };

            // Tạo hàng tiêu đề
            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cell(1, i + 1).Value = headers[i];
            }

            // Định dạng hàng tiêu đề
            var headerRange = worksheet.Range(1, 1, 1, headers.Length);

            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor =
                XLColor.LightBlue;
            headerRange.Style.Alignment.Horizontal =
                XLAlignmentHorizontalValues.Center;
            headerRange.Style.Alignment.Vertical =
                XLAlignmentVerticalValues.Center;

            // Thêm một dòng dữ liệu mẫu để người dùng tham khảo
            worksheet.Cell(2, 1).Value = "Nguyễn Văn A";
            worksheet.Cell(2, 2).Value = "nguyenvana@example.com";
            worksheet.Cell(2, 3).Value = "15/05/1990";
            worksheet.Cell(2, 4).Value = "Nam";
            worksheet.Cell(2, 5).Value = "Xóm 1, Xã A, Tỉnh B";
            worksheet.Cell(2, 6).Value = "Đang làm";
            worksheet.Cell(2, 7).Value = "012345678901";
            worksheet.Cell(2, 8).Value = "";
            worksheet.Cell(2, 9).Value = "Không";
            worksheet.Cell(2, 10).Value = "0912345678";
            worksheet.Cell(2, 11).Value = "";
            worksheet.Cell(2, 12).Value = "true";
            worksheet.Cell(2, 13).Value = "Xóm 1";
            worksheet.Cell(2, 14).Value = "Xã A";
            worksheet.Cell(2, 15).Value = "Tỉnh B";
            worksheet.Cell(2, 16).Value = "Việt Nam";

            // Định dạng ngày sinh để nhập liệu dễ hơn
            worksheet.Column(3).Style.NumberFormat.Format = "@";

            // Tự động điều chỉnh độ rộng cột
            worksheet.Columns().AdjustToContents();

            // Cố định hàng tiêu đề
            worksheet.SheetView.FreezeRows(1);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            var content = stream.ToArray();

            return File(
                content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "Mau_Nhap_Thu_Thu.xlsx"
            );
        }


        [HttpGet]
        public IActionResult ImportExcel()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportExcel(IFormFile? excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                TempData["ImportError"] = "Vui lòng chọn file Excel.";
                return RedirectToAction(nameof(Index));
            }

            if (Path.GetExtension(excelFile.FileName)
                .Equals(".xlsx", StringComparison.OrdinalIgnoreCase) == false)
            {
                TempData["ImportError"] = "Chỉ chấp nhận file Excel định dạng .xlsx.";
                return RedirectToAction(nameof(Index));
            }

            if (excelFile.Length > 10 * 1024 * 1024)
            {
                TempData["ImportError"] = "File Excel không được vượt quá 10 MB.";
                return RedirectToAction(nameof(Index));
            }

            var errors = new List<string>();

            try
            {
                using var stream = new MemoryStream();
                await excelFile.CopyToAsync(stream);
                stream.Position = 0;

                using var workbook = new XLWorkbook(stream);
                var worksheet = workbook.Worksheets
                    .FirstOrDefault(s => s.Name == "ThuThu")
                    ?? workbook.Worksheets.First();

                var usedRange = worksheet.RangeUsed();

                if (usedRange == null)
                {
                    TempData["ImportError"] = "File Excel không có dữ liệu.";
                    return RedirectToAction(nameof(Index));
                }

                // Đọc tên cột ở dòng đầu tiên.
                var headers = new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var cell in worksheet.Row(1).CellsUsed())
                {
                    var header = cell.GetString().Trim();

                    if (!string.IsNullOrWhiteSpace(header))
                        headers[header] = cell.Address.ColumnNumber;
                }

                string[] requiredHeaders =
                {
            "FullName", "Email", "Birth", "Gender", "Address",
            "StatusLibrarian", "CCCD", "Nation", "Religion",
            "NumberPhone", "NumberBHXH", "IsActive",
            "Hamlet", "Commune", "Province", "Nationality"
        };

                var missingHeaders = requiredHeaders
                    .Where(h => !headers.ContainsKey(h))
                    .ToList();

                if (missingHeaders.Any())
                {
                    TempData["ImportError"] =
                        "Thiếu các cột bắt buộc: " +
                        string.Join(", ", missingHeaders);

                    return RedirectToAction(nameof(Index));
                }

                string GetText(IXLRow row, string column)
                {
                    var cell = row.Cell(headers[column]);

                    if (cell.IsEmpty())
                        return string.Empty;

                    // Giữ nguyên cách hiển thị của Excel, đặc biệt với mã/số điện thoại.
                    return cell.GetFormattedString().Trim();
                }

                bool TryGetBirth(IXLRow row, out DateTime? birth)
                {
                    birth = null;
                    var cell = row.Cell(headers["Birth"]);

                    if (cell.IsEmpty())
                        return true;

                    if (cell.TryGetValue<DateTime>(out var date))
                    {
                        birth = date;
                        return date <= DateTime.Today;
                    }

                    var value = cell.GetString().Trim();

                    if (DateTime.TryParseExact(
                        value,
                        new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd" },
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out date)
                        && date <= DateTime.Today)
                    {
                        birth = date;
                        return true;
                    }

                    return false;
                }

                bool TryGetActive(IXLRow row, out bool active)
                {
                    active = true;
                    var value = GetText(row, "IsActive");

                    if (string.IsNullOrWhiteSpace(value))
                        return true;

                    if (bool.TryParse(value, out active))
                        return true;

                    if (value == "1" ||
                        value.Equals("Có", StringComparison.OrdinalIgnoreCase))
                    {
                        active = true;
                        return true;
                    }

                    if (value == "0" ||
                        value.Equals("Không", StringComparison.OrdinalIgnoreCase))
                    {
                        active = false;
                        return true;
                    }

                    return false;
                }

                var pending = new List<tblLibrarian>();
                var emailInFile = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                for (int rowNumber = 2;
                     rowNumber <= usedRange.RangeAddress.LastAddress.RowNumber;
                     rowNumber++)
                {
                    var row = worksheet.Row(rowNumber);

                    if (row.CellsUsed().All(c => string.IsNullOrWhiteSpace(c.GetString())))
                        continue;

                    var fullName = GetText(row, "FullName");
                    var email = GetText(row, "Email");

                    if (string.IsNullOrWhiteSpace(fullName))
                        errors.Add($"Dòng {rowNumber}: Thiếu họ và tên.");

                    if (string.IsNullOrWhiteSpace(email) ||
                        !new EmailAddressAttribute().IsValid(email))
                    {
                        errors.Add($"Dòng {rowNumber}: Email không hợp lệ.");
                    }
                    else if (!emailInFile.Add(email))
                    {
                        errors.Add($"Dòng {rowNumber}: Email {email} bị trùng trong file.");
                    }

                    if (!TryGetBirth(row, out var birth))
                        errors.Add($"Dòng {rowNumber}: Ngày sinh không hợp lệ hoặc ở tương lai.");

                    if (!TryGetActive(row, out var isActive))
                        errors.Add($"Dòng {rowNumber}: IsActive chỉ nhận true/false, 1/0, Có/Không.");

                    if (string.IsNullOrWhiteSpace(fullName) ||
                        string.IsNullOrWhiteSpace(email) ||
                        !new EmailAddressAttribute().IsValid(email) ||
                        !TryGetBirth(row, out birth) ||
                        !TryGetActive(row, out isActive))
                    {
                        continue;
                    }

                    pending.Add(new tblLibrarian
                    {
                        FullName = fullName,
                        Email = email,
                        Birth = birth,
                        Gender = GetText(row, "Gender"),
                        Address = GetText(row, "Address"),
                        StatusLibrarian = GetText(row, "StatusLibrarian"),
                        CCCD = GetText(row, "CCCD"),
                        Nation = GetText(row, "Nation"),
                        Religion = GetText(row, "Religion"),
                        NumberPhone = GetText(row, "NumberPhone"),
                        NumberBHXH = GetText(row, "NumberBHXH"),
                        IsActive = isActive,
                        Hamlet = GetText(row, "Hamlet"),
                        Commune = GetText(row, "Commune"),
                        Province = GetText(row, "Province"),
                        Nationality = GetText(row, "Nationality"),
                        Images = null
                    });
                }

                if (!pending.Any() && !errors.Any())
                    errors.Add("File không có dòng thủ thư hợp lệ để nhập.");

                // Kiểm tra email đã tồn tại trong hệ thống.
                var emailSet = emailInFile;

                var existingUserEmails = await _context.tblUsers
                    .Where(u => u.Email != null)
                    .Select(u => u.Email!)
                    .ToListAsync();

                var existingLibrarianEmails = await _context.Librarians
                    .Where(l => l.Email != null)
                    .Select(l => l.Email!)
                    .ToListAsync();

                var existingEmails = new HashSet<string>(
                    existingUserEmails.Concat(existingLibrarianEmails),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var librarian in pending)
                {
                    if (existingEmails.Contains(librarian.Email!))
                    {
                        errors.Add(
                            $"Email {librarian.Email} đã tồn tại trong hệ thống.");
                    }
                }

                if (errors.Any())
                {
                    TempData["ImportError"] =
                        "Không thể nhập Excel. Vui lòng sửa dữ liệu rồi thử lại.";

                    TempData["ImportDetails"] = string.Join("\n", errors.Take(100));

                    return RedirectToAction(nameof(Index));
                }

                var librarianRole = await _context.tblRoles
                    .FirstOrDefaultAsync(r => r.RoleName == "Librarian");

                if (librarianRole == null)
                {
                    TempData["ImportError"] =
                        "Chưa tồn tại vai trò Librarian trong hệ thống.";

                    return RedirectToAction(nameof(Index));
                }

                await using var transaction =
                    await _context.Database.BeginTransactionAsync();

                try
                {
                    var passwordHasher = new PasswordHasher<tblUsers>();
                    var users = new List<tblUsers>();

                    foreach (var librarian in pending)
                    {
                        var user = new tblUsers
                        {
                            UserName = librarian.FullName,
                            Email = librarian.Email,
                            IsActive = librarian.IsActive
                        };

                        // Giữ cách tạo mật khẩu mặc định như Create() hiện tại.
                        user.Password = passwordHasher.HashPassword(user, "123456@");
                        users.Add(user);
                    }

                    _context.tblUsers.AddRange(users);
                    await _context.SaveChangesAsync();

                    var userRoles = new List<tblUsersRoles>();

                    for (int i = 0; i < pending.Count; i++)
                    {
                        pending[i].UserID = users[i].UserID;

                        userRoles.Add(new tblUsersRoles
                        {
                            UserID = users[i].UserID,
                            RoleID = librarianRole.RoleID
                        });
                    }

                    _context.Librarians.AddRange(pending);
                    _context.tblUsersRoles.AddRange(userRoles);

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    TempData["ImportSuccess"] =
                        $"Nhập thành công {pending.Count} thủ thư.";

                    return RedirectToAction(nameof(Index));
                }
                catch
                {
                    await transaction.RollbackAsync();

                    TempData["ImportError"] =
                        "Có lỗi khi lưu dữ liệu. Giao dịch đã được hoàn tác.";

                    return RedirectToAction(nameof(Index));
                }
            }
            catch (Exception ex)
            {
                TempData["ImportError"] =
                    "Không thể đọc file Excel: " + ex.Message;

                return RedirectToAction(nameof(Index));
            }
        }

        // =========================================================
        // DELETE - GET
        // =========================================================
        public IActionResult Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var lb = _context.Librarians.Find(id);

            if (lb == null)
                return NotFound();

            // Không cho phép xóa Librarian Online
            if (string.Equals(
                lb.FullName?.Trim(),
                "Librarian Online",
                StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Không thể xóa thủ thư hệ thống.";
                return RedirectToAction(nameof(Index));
            }

            return View(lb);
        }

        // =========================================================
        // DELETE - POST
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var delLibrarian = await _context.Librarians
                .FirstOrDefaultAsync(l => l.LibrarianID == id);

            if (delLibrarian == null)
                return NotFound();

            // KHÔNG CHO PHÉP XÓA LIBRARIAN ONLINE
            if (string.Equals(
                delLibrarian.FullName?.Trim(),
                "Librarian Online",
                StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Không thể xóa thủ thư hệ thống.";
                return RedirectToAction(nameof(Index));
            }

            // =========================================================
            // KIỂM TRA USERID
            // =========================================================
            if (!delLibrarian.UserID.HasValue)
            {
                // Librarian cũ chưa liên kết với User
                _context.Librarians.Remove(delLibrarian);
                await _context.SaveChangesAsync();

                if (!string.IsNullOrEmpty(delLibrarian.Images))
                {
                    DeletePhoto(delLibrarian.Images);
                }

                TempData["Success"] =
                    "Đã xóa thủ thư. Thủ thư này chưa được liên kết với tài khoản User.";

                return RedirectToAction(nameof(Index));
            }

            int userId = delLibrarian.UserID.Value;

            // =========================================================
            // KHÔNG CHO XÓA USER ĐANG ĐĂNG NHẬP
            // =========================================================
            var currentUserId =
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (currentUserId == userId.ToString())
            {
                TempData["Error"] =
                    "Bạn không thể xóa tài khoản đang đăng nhập.";

                return RedirectToAction(nameof(Index));
            }

            // =========================================================
            // TRANSACTION
            // =========================================================
            await using var transaction =
                await _context.Database.BeginTransactionAsync();

            try
            {
                // =====================================================
                // 1. LẤY USER
                // =====================================================
                var user = await _context.tblUsers
                    .FirstOrDefaultAsync(u => u.UserID == userId);

                // =====================================================
                // 2. XÓA TẤT CẢ USER ROLES
                // =====================================================
                var userRoles = await _context.tblUsersRoles
                    .Where(ur => ur.UserID == userId)
                    .ToListAsync();

                if (userRoles.Any())
                {
                    _context.tblUsersRoles.RemoveRange(userRoles);
                }

                // =====================================================
                // 3. XÓA LIBRARIAN
                // =====================================================
                _context.Librarians.Remove(delLibrarian);

                // =====================================================
                // 4. XÓA USER
                // =====================================================
                if (user != null)
                {
                    _context.tblUsers.Remove(user);
                }

                // =====================================================
                // 5. LƯU DATABASE
                // =====================================================
                await _context.SaveChangesAsync();

                // =====================================================
                // 6. COMMIT
                // =====================================================
                await transaction.CommitAsync();

                // =====================================================
                // 7. XÓA ẢNH SAU KHI DATABASE XÓA THÀNH CÔNG
                // =====================================================
                if (!string.IsNullOrEmpty(delLibrarian.Images))
                {
                    DeletePhoto(delLibrarian.Images);
                }

                TempData["Success"] =
                    "Đã xóa thủ thư, tài khoản User và toàn bộ vai trò thành công.";

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                TempData["Error"] =
                    "Không thể xóa thủ thư: " + ex.Message;

                return RedirectToAction(nameof(Index));
            }
        }

        // =========================================================
        // CREATE - GET
        // =========================================================
        public IActionResult Create()
        {
            return View();
        }

        // =========================================================
        // CREATE - POST
        // =========================================================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(tblLibrarian model, IFormFile? photo)
        {
            // 1. Kiểm tra mã thủ thư
            if (model.LibrarianID != 0 &&
                await _context.Librarians.AnyAsync(
                    x => x.LibrarianID == model.LibrarianID))
            {
                ModelState.AddModelError(
                    nameof(model.LibrarianID),
                    "Mã thủ thư này đã tồn tại trong hệ thống.");
            }

            // 2. Kiểm tra email trùng trong User và Librarian
            if (!string.IsNullOrWhiteSpace(model.Email))
            {
                model.Email = model.Email.Trim();

                string email = model.Email;

                bool emailExistsInUsers = await _context.tblUsers
                    .AnyAsync(u => u.Email != null && u.Email == email);

                bool emailExistsInLibrarians = await _context.Librarians
                    .AnyAsync(l => l.Email != null && l.Email == email);

                if (emailExistsInUsers || emailExistsInLibrarians)
                {
                    ModelState.AddModelError(
                        nameof(model.Email),
                        "Email này đã tồn tại trong hệ thống. Vui lòng sử dụng email khác.");
                }
            }

            // 3. Upload ảnh nếu người dùng đã chọn
            string? uploadedImagePath = null;

            if (photo != null && photo.Length > 0)
            {
                uploadedImagePath = await SaveImagesAsync(photo);
                model.Images = uploadedImagePath;
            }

            // 4. Nếu dữ liệu không hợp lệ, trả lại form và thông báo lỗi
            if (!ModelState.IsValid)
            {
                ViewBag.TempPhotoUrl = model.Images;
                return View(model);
            }

            await using var transaction =
                await _context.Database.BeginTransactionAsync();

            try
            {
                // 5. Tạo tài khoản User
                var user = new tblUsers
                {
                    UserName = model.FullName,
                    Email = model.Email,
                    IsActive = model.IsActive
                };

                var passwordHasher = new PasswordHasher<tblUsers>();
                user.Password = passwordHasher.HashPassword(user, "123456@");

                _context.tblUsers.Add(user);
                await _context.SaveChangesAsync();

                // 6. Tìm vai trò Librarian
                var librarianRole = await _context.tblRoles
                    .FirstOrDefaultAsync(r => r.RoleName == "Librarian");

                if (librarianRole == null)
                {
                    throw new Exception(
                        "Chưa tồn tại Role 'Librarian' trong tblRoles.");
                }

                // 7. Tạo liên kết vai trò
                _context.tblUsersRoles.Add(new tblUsersRoles
                {
                    UserID = user.UserID,
                    RoleID = librarianRole.RoleID
                });

                // 8. Liên kết tài khoản với hồ sơ thủ thư
                model.UserID = user.UserID;
                _context.Librarians.Add(model);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                TempData["Success"] =
                    "Tạo thủ thư, tài khoản và quyền Librarian thành công.";

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();

                // Xóa ảnh nếu việc tạo thủ thư thất bại
                if (!string.IsNullOrEmpty(uploadedImagePath))
                {
                    DeleteImagesFile(uploadedImagePath);
                }

                model.Images = null;
                ViewBag.TempPhotoUrl = null;

                ModelState.AddModelError(
                    string.Empty,
                    "Có lỗi xảy ra khi tạo thủ thư: " + ex.Message);

                return View(model);
            }
        }

        // =========================================================
        // EDIT - GET
        // =========================================================
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var lb = await _context.Librarians.FindAsync(id);

            if (lb == null)
                return NotFound();

            // Không cho phép chỉnh sửa Librarian Online
            if (await IsSystemLibrarian(id.Value))
            {
                TempData["Error"] = "Không thể chỉnh sửa thủ thư hệ thống.";
                return RedirectToAction("Index");
            }

            return View(lb);
        }


        // =========================================================
        // EDIT - POST
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
    tblLibrarian model,
    IFormFile? photo)
        {
            // =========================================================
            // 1. LẤY THỦ THƯ HIỆN TẠI
            // =========================================================
            var existingLibrarian = await _context.Librarians
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.LibrarianID == model.LibrarianID);

            if (existingLibrarian == null)
                return NotFound();

            // Không cho phép chỉnh sửa Librarian Online
            if (string.Equals(
                existingLibrarian.FullName?.Trim(),
                "Librarian Online",
                StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Không thể chỉnh sửa thủ thư hệ thống.";
                return RedirectToAction(nameof(Index));
            }

            // Kiểm tra email trùng khi sửa thủ thư
            if (!string.IsNullOrWhiteSpace(model.Email))
            {
                model.Email = model.Email.Trim();
                string email = model.Email;

                // Email của hồ sơ thủ thư khác
                bool emailExistsInOtherLibrarians = await _context.Librarians
                    .AnyAsync(l =>
                        l.Email != null &&
                        l.Email == email &&
                        l.LibrarianID != model.LibrarianID);

                // Email của tài khoản User khác
                bool emailExistsInOtherUsers;

                if (existingLibrarian.UserID.HasValue)
                {
                    int currentUserId = existingLibrarian.UserID.Value;

                    emailExistsInOtherUsers = await _context.tblUsers
                        .AnyAsync(u =>
                            u.Email != null &&
                            u.Email == email &&
                            u.UserID != currentUserId);
                }
                else
                {
                    // Hồ sơ chưa liên kết User nên email của mọi User
                    // đều được xem là email đã sử dụng.
                    emailExistsInOtherUsers = await _context.tblUsers
                        .AnyAsync(u => u.Email != null && u.Email == email);
                }

                if (emailExistsInOtherLibrarians || emailExistsInOtherUsers)
                {
                    ModelState.AddModelError(
                        nameof(model.Email),
                        "Email này đã được sử dụng. Vui lòng nhập email khác.");
                }
            }


            // =========================================================
            // 2. KIỂM TRA MODEL
            // =========================================================
            if (!ModelState.IsValid)
            {
                model.Images = existingLibrarian.Images;
                model.UserID = existingLibrarian.UserID;

                return View(model);
            }

            // =========================================================
            // 3. XỬ LÝ ẢNH
            // =========================================================
            if (photo != null && photo.Length > 0)
            {
                var newImagePath = await SavePhotoAsync(photo);

                if (!ModelState.IsValid ||
                    string.IsNullOrEmpty(newImagePath))
                {
                    model.Images = existingLibrarian.Images;
                    return View(model);
                }

                model.Images = newImagePath;
            }
            else
            {
                // Không chọn ảnh mới -> giữ ảnh cũ
                model.Images = existingLibrarian.Images;
            }

            // =========================================================
            // 4. GIỮ USERID
            // =========================================================
            model.UserID = existingLibrarian.UserID;

            // =========================================================
            // 5. CẬP NHẬT LIBRARIAN
            // =========================================================
            _context.Librarians.Update(model);

            // =========================================================
            // 6. ĐỒNG BỘ ISACTIVE SANG USER
            // =========================================================
            if (existingLibrarian.UserID.HasValue)
            {
                var user = await _context.tblUsers
                    .FirstOrDefaultAsync(u =>
                        u.UserID == existingLibrarian.UserID.Value);

                if (user != null)
                {
                    user.UserName = model.FullName;
                    user.Email = model.Email;
                    user.IsActive = model.IsActive;

                    _context.tblUsers.Update(user);
                }
            }

            // =========================================================
            // 7. LƯU DATABASE
            // =========================================================
            await _context.SaveChangesAsync();

            // =========================================================
            // 8. XÓA ẢNH CŨ SAU KHI CẬP NHẬT THÀNH CÔNG
            // =========================================================
            if (photo != null &&
                photo.Length > 0 &&
                !string.IsNullOrEmpty(existingLibrarian.Images))
            {
                DeletePhoto(existingLibrarian.Images);
            }


            TempData["Success"] =
                "Cập nhật thông tin thủ thư thành công.";

            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // LƯU ẢNH
        // =========================================================
        private async Task<string> SaveImagesAsync(IFormFile imageFile)
        {
            string uploadFolder = Path.Combine(
                _environment.WebRootPath,
                "images",
                "librarians");

            if (!Directory.Exists(uploadFolder))
            {
                Directory.CreateDirectory(uploadFolder);
            }

            string extension =
                Path.GetExtension(imageFile.FileName)
                    .ToLowerInvariant();

            string fileName =
                Guid.NewGuid().ToString("N") + extension;

            string filePath =
                Path.Combine(uploadFolder, fileName);

            using (var stream = new FileStream(
                filePath,
                FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            // Lưu đường dẫn tương đối vào database
            return "/images/librarians/" + fileName;
        }

        // =========================================================
        // XÓA FILE ẢNH
        // =========================================================
        private void DeleteImagesFile(string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath))
                    return;

                string fileName =
                    Path.GetFileName(imagePath);

                string filePath = Path.Combine(
                    _environment.WebRootPath,
                    "images",
                    "librarians",
                    fileName);

                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }
            catch
            {
                // Không để lỗi xóa ảnh làm hỏng thao tác chính
            }
        }

        // =========================================================
        // STATISTIC
        // =========================================================
        public IActionResult Statistic()
        {
            try
            {
                var now = DateTime.Now;

                var totalLibrarians =
                    _context.Librarians.Count();

                var totalMinorityLibrarian =
                    _context.Librarians
                        .Count(g => !string.IsNullOrEmpty(g.Nation));

                var ageStats = _context.Librarians
                    .Where(g => g.Birth != null)
                    .AsEnumerable()
                    .GroupBy(g =>
                    {
                        var age =
                            now.Year - g.Birth.Value.Year;

                        if (g.Birth.Value.Date >
                            now.AddYears(-age))
                        {
                            age--;
                        }

                        return (age / 5) * 5;
                    })
                    .Select(g => new
                    {
                        AgeRange =
                            $"{g.Key}-{g.Key + 4}",
                        Count = g.Count()
                    })
                    .OrderBy(g => g.AgeRange)
                    .ToDictionary(
                        g => g.AgeRange,
                        g => g.Count);

                var genderStats = _context.Librarians
                    .GroupBy(g =>
                        string.IsNullOrEmpty(g.Gender)
                            ? "Không xác định"
                            : g.Gender)
                    .Select(g => new
                    {
                        Gender = g.Key,
                        Count = g.Count()
                    })
                    .OrderByDescending(g => g.Count)
                    .ToDictionary(
                        g => g.Gender,
                        g => g.Count);

                var allReligions = _context.Librarians
                    .Where(g =>
                        !string.IsNullOrEmpty(g.Religion))
                    .GroupBy(g => g.Religion)
                    .Select(g => new
                    {
                        Religion = g.Key,
                        Count = g.Count()
                    })
                    .ToList();

                var religionStats = allReligions
                    .Where(r => r.Count > 2)
                    .OrderByDescending(r => r.Count)
                    .ToDictionary(
                        r => r.Religion,
                        r => r.Count);

                var otherReligionsCount =
                    allReligions
                        .Where(r => r.Count <= 2)
                        .Sum(r => r.Count);

                if (otherReligionsCount > 0)
                {
                    religionStats.Add(
                        "Khác",
                        otherReligionsCount);
                }

                var statusStats = _context.Librarians
                    .GroupBy(g =>
                        string.IsNullOrEmpty(g.StatusLibrarian)
                            ? "Không xác định"
                            : g.StatusLibrarian)
                    .Select(g => new
                    {
                        Status = g.Key,
                        Count = g.Count()
                    })
                    .OrderByDescending(g => g.Count)
                    .ToDictionary(
                        g => g.Status,
                        g => g.Count);

                var provinceStats = _context.Librarians
                    .Where(g =>
                        !string.IsNullOrEmpty(g.Province))
                    .GroupBy(g => g.Province)
                    .Select(g => new
                    {
                        Province = g.Key,
                        Count = g.Count()
                    })
                    .OrderByDescending(g => g.Count)
                    .Take(10)
                    .ToDictionary(
                        g => g.Province,
                        g => g.Count);

                var otherProvincesCount =
                    _context.Librarians.Count(
                        g => !string.IsNullOrEmpty(g.Province))
                    - provinceStats.Values.Sum();

                if (otherProvincesCount > 0)
                {
                    provinceStats.Add(
                        "Tỉnh/TP khác",
                        otherProvincesCount);
                }

                ViewBag.TotalTeachers =
                    totalLibrarians;

                ViewBag.TotalMinorityTeachers =
                    totalMinorityLibrarian;

                ViewBag.AgeStats = ageStats;
                ViewBag.GenderStats = genderStats;
                ViewBag.ReligionStats = religionStats;
                ViewBag.StatusStats = statusStats;
                ViewBag.ProvinceStats = provinceStats;

                ViewBag.Religions = religionStats;
                ViewBag.StatusGV = statusStats;
                ViewBag.Genders = genderStats;

                return View();
            }
            catch (Exception)
            {
                return StatusCode(
                    500,
                    "An error occurred while processing your request.");
            }
        }


        // =========================================================
        // TOGGLE STATUS
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            // 1. LẤY LIBRARIAN
            var librarian = await _context.Librarians
                .FirstOrDefaultAsync(l => l.LibrarianID == id);

            if (librarian == null)
                return NotFound();

            // 2. BẢO VỆ LIBRARIAN ONLINE
            if (string.Equals(
                librarian.FullName?.Trim(),
                "Librarian Online",
                StringComparison.OrdinalIgnoreCase))
            {
                // Luôn đảm bảo thủ thư hệ thống được kích hoạt
                librarian.IsActive = true;

                // Đồng bộ trạng thái sang tài khoản User
                if (librarian.UserID.HasValue)
                {
                    var systemUser = await _context.tblUsers
                        .FirstOrDefaultAsync(u =>
                            u.UserID == librarian.UserID.Value);

                    if (systemUser != null)
                    {
                        systemUser.IsActive = true;
                    }
                }

                await _context.SaveChangesAsync();

                TempData["Error"] =
                    "Librarian Online luôn được duy trì trạng thái hoạt động.";

                return RedirectToAction(nameof(Index));
            }

            // 3. ĐẢO TRẠNG THÁI LIBRARIAN THÔNG THƯỜNG
            librarian.IsActive = !librarian.IsActive;

            // 4. ĐỒNG BỘ SANG TÀI KHOẢN USER
            if (librarian.UserID.HasValue)
            {
                var user = await _context.tblUsers
                    .FirstOrDefaultAsync(u =>
                        u.UserID == librarian.UserID.Value);

                if (user != null)
                {
                    user.IsActive = librarian.IsActive;
                }
            }

            // 5. LƯU DATABASE
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }


        private async Task<string?> SavePhotoAsync(IFormFile photo)
        {
            string[] allowedExtensions =
            {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp"
    };

            const long maxFileSize = 5 * 1024 * 1024; // 5MB

            var extension = Path
                .GetExtension(photo.FileName)
                .ToLowerInvariant();

            // Kiểm tra định dạng
            if (!allowedExtensions.Contains(extension))
            {
                ModelState.AddModelError(
                    "Photo",
                    "Ảnh phải có định dạng JPG, JPEG, PNG, GIF hoặc WEBP.");

                return null;
            }

            // Kiểm tra dung lượng
            if (photo.Length > maxFileSize)
            {
                ModelState.AddModelError(
                    "Photo",
                    "Dung lượng ảnh không được vượt quá 5MB.");

                return null;
            }

            // Kiểm tra Content-Type
            if (string.IsNullOrWhiteSpace(photo.ContentType) ||
                !photo.ContentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(
                    "Photo",
                    "File được chọn không phải là hình ảnh.");

                return null;
            }

            // Thư mục wwwroot/uploads/librarians
            var uploadFolder = Path.Combine(
                _environment.WebRootPath,
                "uploads",
                "librarians");

            if (!Directory.Exists(uploadFolder))
            {
                Directory.CreateDirectory(uploadFolder);
            }

            // Tạo tên file mới để tránh trùng
            var fileName =
                $"{Guid.NewGuid():N}{extension}";

            var filePath = Path.Combine(
                uploadFolder,
                fileName);

            await using (var stream =
                new FileStream(filePath, FileMode.Create))
            {
                await photo.CopyToAsync(stream);
            }

            // Đường dẫn lưu vào database
            return $"/uploads/librarians/{fileName}";
        }

        private void DeletePhoto(string? photoPath)
        {
            if (string.IsNullOrWhiteSpace(photoPath))
                return;

            const string folder =
                "/uploads/librarians/";

            if (!photoPath.StartsWith(
                folder,
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var relativePath =
                photoPath.TrimStart('/')
                         .Replace(
                             '/',
                             Path.DirectorySeparatorChar);

            var filePath = Path.Combine(
                _environment.WebRootPath,
                relativePath);

            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }
        }

        private async Task<bool> IsSystemLibrarian(int id)
        {
            return await _context.Librarians.AnyAsync(l =>
                l.LibrarianID == id &&
                l.FullName == "Librarian Online");
        }

    }
}