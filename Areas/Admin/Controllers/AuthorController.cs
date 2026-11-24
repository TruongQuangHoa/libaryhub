using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ClosedXML.Excel;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class AuthorController : Controller
    {
        private readonly DataContext _context;
        private readonly IWebHostEnvironment _environment;

        public AuthorController(
            DataContext context,
            IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        // =========================================================
        // INDEX
        // =========================================================
        public async Task<IActionResult> Index()
        {
            var authors = await _context.Authors
                .Include(a => a.Publisher)
                .OrderBy(a => a.AuthorID)
                .ToListAsync();

            return View(authors);
        }

        // =========================================================
        // CREATE - GET
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            await LoadPublishersAsync();

            return View(new tblAuthor
            {
                IsActive = true,
                BookCount = 0,
                TotalReaders = 0,
                Rating = null,
                CreateDate = DateTime.Now
            });
        }

        // =========================================================
        // CREATE - POST
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(tblAuthor author, IFormFile? imageFile)
        {
            // Kiểm tra nhà xuất bản được chọn có tồn tại hay không.
            if (author.PublisherID.HasValue)
            {
                bool publisherExists = await _context.Publishers
                    .AnyAsync(p => p.PublisherID == author.PublisherID.Value);

                if (!publisherExists)
                {
                    ModelState.AddModelError(
                        "PublisherID",
                        "Nhà xuất bản được chọn không tồn tại."
                    );
                }
            }

            if (author.PublisherID == null || !await _context.Publishers.AnyAsync(x =>
                x.PublisherID == author.PublisherID.Value &&
                x.IsActive))
            {
                ModelState.AddModelError(
                    "PublisherID",
                    "Vui lòng chọn một nhà xuất bản đang hoạt động."
                );
            }

            if (!ModelState.IsValid)
            {
                await LoadPublishersAsync(author.PublisherID);
                return View(author);
            }

            author.Rating = null;
            author.CreateDate = DateTime.Now;
            author.UpdateDate = null;

            if (imageFile != null && imageFile.Length > 0)
            {
                var imagePath = await SaveAuthorImageAsync(imageFile);

                if (imagePath == null)
                {
                    await LoadPublishersAsync(author.PublisherID);
                    return View(author);
                }

                author.ImageAuthor = imagePath;
            }

            _context.Authors.Add(author);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Thêm tác giả thành công.";
            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // EDIT - GET
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null || id <= 0)
                return NotFound();

            var author = await _context.Authors
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.AuthorID == id.Value);

            if (author == null)
                return NotFound();

            await LoadPublishersAsync(author.PublisherID);

            return View(author);
        }

        // =========================================================
        // EDIT - POST
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(tblAuthor model, IFormFile? imageFile)
        {
            var existingAuthor = await _context.Authors
                .FirstOrDefaultAsync(a => a.AuthorID == model.AuthorID);

            if (existingAuthor == null)
                return NotFound();

            // Kiểm tra nhà xuất bản được chọn.
            if (model.PublisherID.HasValue)
            {
                bool publisherExists = await _context.Publishers
                    .AnyAsync(p => p.PublisherID == model.PublisherID.Value);

                if (!publisherExists)
                {
                    ModelState.AddModelError(
                        "PublisherID",
                        "Nhà xuất bản được chọn không tồn tại."
                    );
                }
            }

            if (model.PublisherID == null ||
    !await _context.Publishers.AnyAsync(x =>
        x.PublisherID == model.PublisherID.Value &&
        x.IsActive))
            {
                ModelState.AddModelError(
                    "PublisherID",
                    "Vui lòng chọn một nhà xuất bản đang hoạt động."
                );
            }

            // Giữ lại dữ liệu hệ thống.
            model.Rating = existingAuthor.Rating;
            model.CreateDate = existingAuthor.CreateDate;
            model.UpdateDate = DateTime.Now;
            model.ImageAuthor = existingAuthor.ImageAuthor;

            if (!ModelState.IsValid)
            {
                await LoadPublishersAsync(model.PublisherID);
                return View(model);
            }

            string? newImagePath = null;

            if (imageFile != null && imageFile.Length > 0)
            {
                newImagePath = await SaveAuthorImageAsync(imageFile);

                if (newImagePath == null)
                {
                    await LoadPublishersAsync(model.PublisherID);
                    return View(model);
                }
            }

            // Cập nhật thông tin tác giả.
            existingAuthor.FullName = model.FullName;
            existingAuthor.DateOfBirth = model.DateOfBirth;
            existingAuthor.Gender = model.Gender;
            existingAuthor.Nationality = model.Nationality;
            existingAuthor.Specialization = model.Specialization;
            existingAuthor.Biography = model.Biography;
            existingAuthor.BookCount = model.BookCount;
            existingAuthor.TotalReaders = model.TotalReaders;
            existingAuthor.IsActive = model.IsActive;

            // Cập nhật nhà xuất bản.
            existingAuthor.PublisherID = model.PublisherID;

            // Chỉ thay ảnh khi người dùng chọn ảnh mới.
            if (!string.IsNullOrEmpty(newImagePath))
            {
                existingAuthor.ImageAuthor = newImagePath;
            }

            existingAuthor.UpdateDate = DateTime.Now;

            await _context.SaveChangesAsync();

            // Xóa ảnh cũ sau khi lưu dữ liệu thành công.
            if (!string.IsNullOrEmpty(newImagePath) &&
                !string.IsNullOrEmpty(model.ImageAuthor))
            {
                DeleteAuthorImage(model.ImageAuthor);
            }

            TempData["Success"] = "Cập nhật thông tin tác giả thành công.";
            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // DELETE - GET
        // =========================================================
        [HttpGet]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null || id <= 0)
                return NotFound();

            var author = await _context.Authors
                .Include(a => a.Publisher)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.AuthorID == id.Value);

            if (author == null)
                return NotFound();

            return View(author);
        }

        // =========================================================
        // DELETE - POST
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var author = await _context.Authors
                .FirstOrDefaultAsync(a => a.AuthorID == id);

            if (author == null)
                return NotFound();

            string? oldImage = author.ImageAuthor;

            _context.Authors.Remove(author);

            try
            {
                await _context.SaveChangesAsync();

                if (!string.IsNullOrEmpty(oldImage))
                    DeleteAuthorImage(oldImage);

                TempData["Success"] = "Xóa tác giả thành công.";
            }
            catch (DbUpdateException)
            {
                // Có thể tác giả đang được tham chiếu bởi dữ liệu sách.
                TempData["Error"] =
                    "Không thể xóa tác giả vì dữ liệu này đang được sử dụng. " +
                    "Vui lòng kiểm tra các sách liên quan trước khi xóa.";
            }

            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // TOGGLE STATUS
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var author = await _context.Authors
                .FirstOrDefaultAsync(a => a.AuthorID == id);

            if (author == null)
                return NotFound();

            author.IsActive = !author.IsActive;
            author.UpdateDate = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = author.IsActive
                ? "Đã kích hoạt tác giả."
                : "Đã ngừng hiển thị tác giả.";

            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // LOAD DANH SÁCH NHÀ XUẤT BẢN
        // =========================================================
        private async Task LoadPublishersAsync(int? selectedPublisherID = null)
        {
            var publishers = await _context.Publishers
                .OrderBy(p => p.PublisherName)
                .ToListAsync();

            ViewBag.PublisherList = new SelectList(
                publishers,
                "PublisherID",
                "PublisherName",
                selectedPublisherID
            );
        }

        // =========================================================
        // SAVE AUTHOR IMAGE
        // =========================================================
        private async Task<string?> SaveAuthorImageAsync(IFormFile imageFile)
        {
            string[] allowedExtensions =
            {
                ".jpg",
                ".jpeg",
                ".png",
                ".gif",
                ".webp"
            };

            const long maxFileSize = 5 * 1024 * 1024;

            string extension = Path.GetExtension(imageFile.FileName)
                .ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
            {
                ModelState.AddModelError(
                    "ImageAuthor",
                    "Ảnh tác giả phải có định dạng JPG, JPEG, PNG, GIF hoặc WEBP.");

                return null;
            }

            if (imageFile.Length > maxFileSize)
            {
                ModelState.AddModelError(
                    "ImageAuthor",
                    "Dung lượng ảnh không được vượt quá 5MB.");

                return null;
            }

            if (string.IsNullOrWhiteSpace(imageFile.ContentType) ||
                !imageFile.ContentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(
                    "ImageAuthor",
                    "File được chọn không phải là hình ảnh.");

                return null;
            }

            string uploadFolder = Path.Combine(
                _environment.WebRootPath,
                "uploads",
                "authors");

            Directory.CreateDirectory(uploadFolder);

            string fileName = $"{Guid.NewGuid():N}{extension}";
            string filePath = Path.Combine(uploadFolder, fileName);

            await using (var stream = new FileStream(
                filePath,
                FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            return $"/uploads/authors/{fileName}";
        }

        // =========================================================
        // DELETE AUTHOR IMAGE
        // =========================================================
        private void DeleteAuthorImage(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return;

            try
            {
                string fileName = Path.GetFileName(imagePath);

                if (string.IsNullOrWhiteSpace(fileName))
                    return;

                string filePath = Path.Combine(
                    _environment.WebRootPath,
                    "uploads",
                    "authors",
                    fileName);

                if (System.IO.File.Exists(filePath))
                    System.IO.File.Delete(filePath);
            }
            catch
            {
                // Không để lỗi xóa ảnh ảnh hưởng đến thao tác database.
            }
        }

        // =========================================================
        // IMPORT AUTHORS FROM EXCEL
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportExcel(IFormFile? excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                TempData["ImportError"] = "Vui lòng chọn file Excel để nhập dữ liệu.";
                return RedirectToAction(nameof(Index));
            }

            const long maxFileSize = 10 * 1024 * 1024;

            if (excelFile.Length > maxFileSize)
            {
                TempData["ImportError"] = "Dung lượng file Excel không được vượt quá 10MB.";
                return RedirectToAction(nameof(Index));
            }

            string extension = Path.GetExtension(excelFile.FileName).ToLowerInvariant();

            if (extension != ".xlsx")
            {
                TempData["ImportError"] = "Vui lòng sử dụng file Excel có định dạng .xlsx.";
                return RedirectToAction(nameof(Index));
            }

            var errors = new List<string>();
            var authorsToImport = new List<tblAuthor>();

            try
            {
                // Lấy danh sách nhà xuất bản đang hoạt động để đối chiếu khi import.
                var publishers = await _context.Publishers
                    .Where(p => p.IsActive)
                    .ToListAsync();

                using (var stream = new MemoryStream())
                {
                    await excelFile.CopyToAsync(stream);
                    stream.Position = 0;

                    using (var workbook = new XLWorkbook(stream))
                    {
                        var worksheet = workbook.Worksheets
                            .FirstOrDefault(ws => ws.Name == "TacGia");

                        if (worksheet == null)
                        {
                            TempData["ImportError"] =
                                "Không tìm thấy sheet 'TacGia' trong file Excel. " +
                                "Vui lòng sử dụng file mẫu của hệ thống.";

                            return RedirectToAction(nameof(Index));
                        }

                        var headerRow = worksheet.FirstRowUsed();

                        if (headerRow == null)
                        {
                            TempData["ImportError"] = "File Excel không có dữ liệu.";
                            return RedirectToAction(nameof(Index));
                        }

                        // Đọc tên cột ở dòng đầu tiên.
                        var headers = new Dictionary<string, int>(
                            StringComparer.OrdinalIgnoreCase);

                        foreach (var cell in headerRow.CellsUsed())
                        {
                            string header = cell.GetString().Trim();

                            if (!string.IsNullOrWhiteSpace(header))
                                headers[header] = cell.Address.ColumnNumber;
                        }

                        string[] requiredHeaders =
                        {
                            "FullName",
                            "PublisherName",
                            "DateOfBirth",
                            "Gender",
                            "Nationality",
                            "Specialization",
                            "Biography",
                            "BookCount",
                            "TotalReaders",
                            "IsActive"
                        };

                        var missingHeaders = requiredHeaders
                            .Where(h => !headers.ContainsKey(h))
                            .ToList();

                        if (missingHeaders.Any())
                        {
                            TempData["ImportError"] =
                                "File Excel thiếu các cột bắt buộc: " +
                                string.Join(", ", missingHeaders);

                            return RedirectToAction(nameof(Index));
                        }

                        int firstDataRow = headerRow.RowNumber() + 1;
                        int lastDataRow = worksheet.LastRowUsed()?.RowNumber()
                                          ?? firstDataRow - 1;

                        for (int row = firstDataRow; row <= lastDataRow; row++)
                        {
                            var currentRow = worksheet.Row(row);

                            // Bỏ qua dòng hoàn toàn trống.
                            if (!currentRow.CellsUsed().Any())
                                continue;

                            string GetValue(string columnName)
                            {
                                return currentRow
                                    .Cell(headers[columnName])
                                    .GetFormattedString()
                                    .Trim();
                            }

                            string fullName = GetValue("FullName");
                            string publisherName = GetValue("PublisherName");
                            string dateOfBirthText = GetValue("DateOfBirth");
                            string gender = GetValue("Gender");
                            string nationality = GetValue("Nationality");
                            string specialization = GetValue("Specialization");
                            string biography = GetValue("Biography");
                            string bookCountText = GetValue("BookCount");
                            string totalReadersText = GetValue("TotalReaders");
                            string isActiveText = GetValue("IsActive");

                            bool rowHasError = false;

                            // Họ tên.
                            if (string.IsNullOrWhiteSpace(fullName))
                            {
                                errors.Add($"Dòng {row}: Chưa nhập họ và tên tác giả.");
                                rowHasError = true;
                            }

                            // Nhà xuất bản bắt buộc.
                            var selectedPublisher = publishers.FirstOrDefault(p =>
                                string.Equals(
                                    p.PublisherName?.Trim(),
                                    publisherName?.Trim(),
                                    StringComparison.OrdinalIgnoreCase));

                            if (string.IsNullOrWhiteSpace(publisherName))
                            {
                                errors.Add($"Dòng {row}: Chưa nhập nhà xuất bản.");
                                rowHasError = true;
                            }
                            else if (selectedPublisher == null)
                            {
                                errors.Add(
                                    $"Dòng {row}: Nhà xuất bản '{publisherName}' không tồn tại " +
                                    "hoặc đang ngừng hoạt động.");
                                rowHasError = true;
                            }

                            // Chuyên môn.
                            if (string.IsNullOrWhiteSpace(specialization))
                            {
                                errors.Add($"Dòng {row}: Chưa nhập lĩnh vực/chuyên môn.");
                                rowHasError = true;
                            }

                            // Tiểu sử.
                            if (string.IsNullOrWhiteSpace(biography))
                            {
                                errors.Add($"Dòng {row}: Chưa nhập tiểu sử tác giả.");
                                rowHasError = true;
                            }

                            // Ngày sinh không bắt buộc.
                            DateTime? dateOfBirth = null;

                            if (!string.IsNullOrWhiteSpace(dateOfBirthText))
                            {
                                DateTime parsedDate;

                                bool validDate =
                                    DateTime.TryParse(
                                        dateOfBirthText,
                                        CultureInfo.GetCultureInfo("vi-VN"),
                                        DateTimeStyles.None,
                                        out parsedDate)
                                    ||
                                    DateTime.TryParse(
                                        dateOfBirthText,
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.None,
                                        out parsedDate);

                                if (!validDate)
                                {
                                    errors.Add($"Dòng {row}: Ngày sinh không hợp lệ.");
                                    rowHasError = true;
                                }
                                else if (parsedDate.Date > DateTime.Today)
                                {
                                    errors.Add($"Dòng {row}: Ngày sinh không được lớn hơn ngày hiện tại.");
                                    rowHasError = true;
                                }
                                else
                                {
                                    dateOfBirth = parsedDate;
                                }
                            }

                            // Giới tính không bắt buộc.
                            if (!string.IsNullOrWhiteSpace(gender) &&
                                !new[] { "Nam", "Nữ", "Khác" }
                                    .Contains(gender, StringComparer.OrdinalIgnoreCase))
                            {
                                errors.Add($"Dòng {row}: Giới tính chỉ được nhập Nam, Nữ hoặc Khác.");
                                rowHasError = true;
                            }

                            // Số đầu sách: dữ liệu hiển thị do quản trị viên nhập.
                            int bookCount = 0;

                            if (!int.TryParse(bookCountText, out bookCount) ||
                                bookCount < 0)
                            {
                                errors.Add($"Dòng {row}: Số đầu sách phải là số nguyên không âm.");
                                rowHasError = true;
                            }

                            // Lượt độc giả: dữ liệu hiển thị do quản trị viên nhập.
                            int totalReaders = 0;

                            if (!int.TryParse(totalReadersText, out totalReaders) ||
                                totalReaders < 0)
                            {
                                errors.Add($"Dòng {row}: Lượt độc giả phải là số nguyên không âm.");
                                rowHasError = true;
                            }

                            // Trạng thái.
                            bool isActive = true;

                            if (string.Equals(isActiveText, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                                isActiveText == "1" ||
                                string.Equals(isActiveText, "Có", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(isActiveText, "Đang hiển thị", StringComparison.OrdinalIgnoreCase))
                            {
                                isActive = true;
                            }
                            else if (
                                string.Equals(isActiveText, "FALSE", StringComparison.OrdinalIgnoreCase) ||
                                isActiveText == "0" ||
                                string.Equals(isActiveText, "Không", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(isActiveText, "Ẩn", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(isActiveText, "Ngừng hiển thị", StringComparison.OrdinalIgnoreCase))
                            {
                                isActive = false;
                            }
                            else
                            {
                                errors.Add($"Dòng {row}: Trạng thái phải là TRUE/FALSE, 1/0, Có/Không hoặc Đang hiển thị/Ẩn.");
                                rowHasError = true;
                            }

                            if (rowHasError)
                                continue;

                            authorsToImport.Add(new tblAuthor
                            {
                                FullName = fullName,
                                // Gán mã nhà xuất bản tìm được trong cơ sở dữ liệu.
                                PublisherID = selectedPublisher!.PublisherID,
                                DateOfBirth = dateOfBirth,
                                Gender = string.IsNullOrWhiteSpace(gender) ? null : gender,
                                Nationality = string.IsNullOrWhiteSpace(nationality) ? null : nationality,
                                Specialization = specialization,
                                Biography = biography,
                                BookCount = bookCount,
                                TotalReaders = totalReaders,
                                ImageAuthor = null,
                                Rating = null,
                                IsActive = isActive,
                                CreateDate = DateTime.Now,
                                UpdateDate = null
                            });
                        }
                    }
                }

                if (errors.Any())
                {
                    TempData["ImportError"] =
                        $"File Excel có {errors.Count} lỗi. Không có tác giả nào được nhập.";

                    TempData["ImportDetails"] = string.Join("\n", errors.Take(50));

                    return RedirectToAction(nameof(Index));
                }

                if (!authorsToImport.Any())
                {
                    TempData["ImportError"] = "File Excel không có dòng dữ liệu tác giả hợp lệ.";
                    return RedirectToAction(nameof(Index));
                }

                // Chỉ lưu khi toàn bộ dữ liệu hợp lệ.
                await using (var transaction = await _context.Database.BeginTransactionAsync())
                {
                    _context.Authors.AddRange(authorsToImport);

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                }

                TempData["ImportSuccess"] =
                    $"Nhập thành công {authorsToImport.Count} tác giả từ file Excel.";
            }
            catch (Exception)
            {
                TempData["ImportError"] =
                    "Không thể đọc file Excel. Vui lòng kiểm tra lại file và sử dụng file mẫu của hệ thống.";
            }

            return RedirectToAction(nameof(Index));
        }

        // =========================================================
        // DOWNLOAD AUTHOR EXCEL TEMPLATE
        // =========================================================
        [HttpGet]
        public IActionResult DownloadExcelTemplate()
        {
            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add("TacGia");

                string[] headers =
                {
                    "FullName",
                    "PublisherName",
                    "DateOfBirth",
                    "Gender",
                    "Nationality",
                    "Specialization",
                    "Biography",
                    "BookCount",
                    "TotalReaders",
                    "IsActive"
                };

                for (int i = 0; i < headers.Length; i++)
                {
                    worksheet.Cell(1, i + 1).Value = headers[i];
                }

                // Dòng dữ liệu minh họa.
                worksheet.Cell(2, 1).Value = "Nguyễn Nhật Ánh";
                worksheet.Cell(2, 2).Value = "Kim Đồng";
                worksheet.Cell(2, 3).Value = new DateTime(1955, 5, 7);
                worksheet.Cell(2, 3).Style.DateFormat.Format = "dd/MM/yyyy";
                worksheet.Cell(2, 4).Value = "Nam";
                worksheet.Cell(2, 5).Value = "Việt Nam";
                worksheet.Cell(2, 6).Value = "Văn học";
                worksheet.Cell(2, 7).Value = "Nhà văn chuyên sáng tác cho thanh thiếu niên.";
                worksheet.Cell(2, 8).Value = 100;
                worksheet.Cell(2, 9).Value = 5000;
                worksheet.Cell(2, 10).Value = true;

                // Định dạng tiêu đề.
                var headerRange = worksheet.Range(1, 1, 1, headers.Length);

                headerRange.Style.Font.Bold = true;
                headerRange.Style.Alignment.Horizontal =
                    XLAlignmentHorizontalValues.Center;

                headerRange.Style.Fill.BackgroundColor =
                    XLColor.LightBlue;

                headerRange.Style.Border.OutsideBorder =
                    XLBorderStyleValues.Thin;

                headerRange.Style.Border.InsideBorder =
                    XLBorderStyleValues.Thin;

                // Định dạng dữ liệu.
                worksheet.Columns().AdjustToContents();

                worksheet.Column(6).Width = 55;
                worksheet.Column(6).Style.Alignment.WrapText = true;

                worksheet.SheetView.FreezeRows(1);
                worksheet.RangeUsed().SetAutoFilter();

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);

                    return File(
                        stream.ToArray(),
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        "Mau_Nhap_Tac_Gia.xlsx");
                }
            }
        }
    }
}
