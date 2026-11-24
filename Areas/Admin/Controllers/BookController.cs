using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ClosedXML.Excel;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Librarian")]
    public class BookController : Controller
    {
        private readonly DataContext _context;
        private readonly IWebHostEnvironment _environment;

        public BookController(
            DataContext context,
            IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        // =========================
        // INDEX
        // =========================
        public IActionResult Index()
        {
            var sbList = _context.Books
                .OrderBy(m => m.BookID)
                .Include(m => m.category)
                .ToList();

            return View(sbList);
        }

        // =========================
        // DELETE - GET
        // =========================
        public IActionResult Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var sb = _context.Books.Find(id);

            if (sb == null)
                return NotFound();

            return View(sb);
        }

        // =========================
        // DELETE - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            var delBook = _context.Books.Find(id);

            if (delBook == null)
                return NotFound();

            // Xóa ảnh cũ
            if (!string.IsNullOrEmpty(delBook.ImageBook))
            {
                DeleteBookImage(delBook.ImageBook);
            }

            _context.Books.Remove(delBook);
            _context.SaveChanges();

            return RedirectToAction(nameof(Index));
        }

        // =========================
        // CREATE - GET
        // =========================
        public IActionResult Create()
        {
            LoadData();

            return View(new tblBook
            {
                IsActive = true,
                BookQuantity = 0,
                BorrowQuantity = 0,
                TotalReaders = 0
            });
        }

        // =========================
        // CREATE - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            tblBook sb,
            IFormFile? imageFile)
        {
            // Kiểm tra nhà xuất bản và tác giả
            bool validAuthor = await ValidatePublisherAndAuthor(sb);

            if (!validAuthor || !ModelState.IsValid)
            {
                LoadData(sb.PublisherID, sb.AuthorID);
                return View(sb);
            }

            // Upload ảnh
            if (imageFile != null && imageFile.Length > 0)
            {
                var imagePath = await SaveBookImageAsync(imageFile);

                if (imagePath == null)
                {
                    LoadData(sb.PublisherID, sb.AuthorID);
                    return View(sb);
                }

                sb.ImageBook = imagePath;
            }

            // Giá trị mặc định khi tạo mới
            sb.BorrowQuantity = 0;
            sb.TotalReaders = 0;

            _context.Books.Add(sb);

            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        /// =========================
        // EDIT - GET
        // =========================
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var book = await _context.Books
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.BookID == id);

            if (book == null)
                return NotFound();

            // Tìm mã nhà xuất bản theo tên đang lưu
            var publisher = await _context.Publishers
                .FirstOrDefaultAsync(x =>
                    x.PublisherName == book.Publisher);

            if (publisher != null)
            {
                book.PublisherID = publisher.PublisherID;

                // Tìm tác giả thuộc nhà xuất bản đó
                var author = await _context.Authors
                    .FirstOrDefaultAsync(x =>
                        x.FullName == book.Author &&
                        x.PublisherID == publisher.PublisherID);

                book.AuthorID = author?.AuthorID;
            }

            LoadData(book.PublisherID, book.AuthorID);

            return View(book);
        }

        // =========================
        // EDIT - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            tblBook sb,
            IFormFile? imageFile)
        {
            // 1. Lấy thông tin sách hiện tại
            var existingBook = await _context.Books
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.BookID == sb.BookID);

            if (existingBook == null)
                return NotFound();

            // 2. Giữ nguyên số liệu do hệ thống quản lý
            sb.BorrowQuantity = existingBook.BorrowQuantity;
            sb.TotalReaders = existingBook.TotalReaders;

            // 3. Kiểm tra nhà xuất bản và tác giả
            bool validAuthor = await ValidatePublisherAndAuthor(sb);

            if (!validAuthor || !ModelState.IsValid)
            {
                sb.ImageBook = existingBook.ImageBook;

                LoadData(sb.PublisherID, sb.AuthorID);

                return View(sb);
            }

            // 4. Xử lý ảnh
            if (imageFile != null && imageFile.Length > 0)
            {
                var newImagePath = await SaveBookImageAsync(imageFile);

                if (newImagePath == null)
                {
                    sb.ImageBook = existingBook.ImageBook;

                    LoadData(sb.PublisherID, sb.AuthorID);

                    return View(sb);
                }

                sb.ImageBook = newImagePath;
            }
            else
            {
                // Không chọn ảnh mới thì giữ ảnh cũ
                sb.ImageBook = existingBook.ImageBook;
            }

            // 5. Cập nhật thông tin sách
            _context.Books.Update(sb);

            await _context.SaveChangesAsync();

            // 6. Xóa ảnh cũ sau khi cập nhật thành công
            if (imageFile != null &&
                imageFile.Length > 0 &&
                !string.IsNullOrEmpty(existingBook.ImageBook))
            {
                DeleteBookImage(existingBook.ImageBook);
            }

            return RedirectToAction(nameof(Index));
        }

        // =========================
        // TOGGLE STATUS
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            var book = await _context.Books.FindAsync(id);

            if (book == null)
                return NotFound();

            book.IsActive = !book.IsActive;

            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        // =========================
        // LOAD CATEGORY, PUBLISHER, AUTHOR
        // =========================
        private void LoadData(
            int? selectedPublisherId = null,
            int? selectedAuthorId = null)
        {
            // Danh sách thể loại
            var dpList = _context.Categories
                .Where(x => x.IsActive)
                .Select(x => new
                {
                    x.CategoryID,
                    Info = x.CategoryID + " - " + x.CategoryName
                })
                .ToList();

            ViewBag.dpList = new SelectList(
                dpList,
                "CategoryID",
                "Info"
            );

            // Danh sách nhà xuất bản
            var publisherList = _context.Publishers
                .Where(x => x.IsActive ||
                            x.PublisherID == selectedPublisherId)
                .OrderBy(x => x.PublisherName)
                .ToList();

            ViewBag.PublisherList = new SelectList(
                publisherList,
                "PublisherID",
                "PublisherName",
                selectedPublisherId
            );

            // Danh sách tác giả thuộc nhà xuất bản đã chọn
            var authorList = new List<tblAuthor>();

            if (selectedPublisherId.HasValue)
            {
                authorList = _context.Authors
                    .Where(x =>
                        x.PublisherID == selectedPublisherId &&
                        (x.IsActive || x.AuthorID == selectedAuthorId))
                    .OrderBy(x => x.FullName)
                    .ToList();
            }

            ViewBag.AuthorList = new SelectList(
                authorList,
                "AuthorID",
                "FullName",
                selectedAuthorId
            );
        }

        // =========================
        // SAVE BOOK IMAGE
        // =========================
        private async Task<string?> SaveBookImageAsync(IFormFile imageFile)
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

            var extension = Path
                .GetExtension(imageFile.FileName)
                .ToLowerInvariant();

            // Kiểm tra extension
            if (!allowedExtensions.Contains(extension))
            {
                ModelState.AddModelError(
                    "ImageBook",
                    "Ảnh sách phải có định dạng JPG, JPEG, PNG, GIF hoặc WEBP."
                );

                return null;
            }

            // Kiểm tra dung lượng
            if (imageFile.Length > maxFileSize)
            {
                ModelState.AddModelError(
                    "ImageBook",
                    "Dung lượng ảnh không được vượt quá 5MB."
                );

                return null;
            }

            // Kiểm tra ContentType
            if (string.IsNullOrWhiteSpace(imageFile.ContentType) ||
                !imageFile.ContentType.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError(
                    "ImageBook",
                    "File được chọn không phải là hình ảnh."
                );

                return null;
            }

            // Tạo thư mục
            var uploadFolder = Path.Combine(
                _environment.WebRootPath,
                "uploads",
                "books"
            );

            if (!Directory.Exists(uploadFolder))
            {
                Directory.CreateDirectory(uploadFolder);
            }

            // Tạo tên file mới
            var fileName = $"{Guid.NewGuid():N}{extension}";

            var filePath = Path.Combine(
                uploadFolder,
                fileName
            );

            await using (var stream = new FileStream(
                filePath,
                FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            return $"/uploads/books/{fileName}";
        }

        // =========================
        // DELETE BOOK IMAGE
        // =========================
        private void DeleteBookImage(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return;

            try
            {
                var fileName = Path.GetFileName(imagePath);

                if (string.IsNullOrWhiteSpace(fileName))
                    return;

                var filePath = Path.Combine(
                    _environment.WebRootPath,
                    "uploads",
                    "books",
                    fileName
                );

                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }
            }
            catch
            {
                // Không để lỗi xóa ảnh làm lỗi thao tác database
            }
        }

        // =========================
        // GET AUTHORS BY PUBLISHER
        // =========================
        [HttpGet]
        public async Task<IActionResult> GetAuthorsByPublisher(int publisherId)
        {
            var authors = await _context.Authors
                .Where(x =>
                    x.PublisherID == publisherId &&
                    x.IsActive)
                .OrderBy(x => x.FullName)
                .Select(x => new
                {
                    id = x.AuthorID,
                    name = x.FullName
                })
                .ToListAsync();

            return Json(authors);
        }

        // =========================
        // VALIDATE PUBLISHER AND AUTHOR
        // =========================
        private async Task<bool> ValidatePublisherAndAuthor(tblBook sb)
        {
            ModelState.Remove(nameof(sb.Publisher));
            ModelState.Remove(nameof(sb.Author));

            var publisher = await _context.Publishers
                .FirstOrDefaultAsync(x =>
                    x.PublisherID == sb.PublisherID);

            if (publisher == null)
            {
                ModelState.AddModelError(
                    nameof(sb.PublisherID),
                    "Vui lòng chọn nhà xuất bản hợp lệ."
                );

                return false;
            }

            var author = await _context.Authors
                .FirstOrDefaultAsync(x =>
                    x.AuthorID == sb.AuthorID &&
                    x.PublisherID == sb.PublisherID);

            if (author == null)
            {
                ModelState.AddModelError(
                    nameof(sb.AuthorID),
                    "Tác giả không thuộc nhà xuất bản đã chọn. Vui lòng chọn lại tác giả."
                );

                return false;
            }

            // Lưu tên nhà xuất bản và tác giả vào bảng tblBook
            sb.Publisher = publisher.PublisherName;
            sb.Author = author.FullName;

            return true;
        }

        // =========================
        // IMPORT EXCEL - GET
        // =========================
        [HttpGet]
        public IActionResult Import()
        {
            return View();
        }

        // =========================
        // IMPORT EXCEL - POST
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Import(IFormFile? excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                ViewBag.Error = "Vui lòng chọn file Excel.";
                return View();
            }

            // Giới hạn dung lượng file 10 MB
            if (excelFile.Length > 10 * 1024 * 1024)
            {
                ViewBag.Error = "File Excel không được vượt quá 10 MB.";
                return View();
            }

            if (!string.Equals(
                Path.GetExtension(excelFile.FileName),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase))
            {
                ViewBag.Error = "Chỉ hỗ trợ file Excel định dạng .xlsx.";
                return View();
            }

            var errors = new List<string>();
            var importedBooks = new List<tblBook>();

            try
            {
                await using var stream = new MemoryStream();
                await excelFile.CopyToAsync(stream);
                stream.Position = 0;

                using var workbook = new XLWorkbook(stream);
                var worksheet = workbook.Worksheets.FirstOrDefault();

                if (worksheet == null)
                {
                    ViewBag.Error = "File Excel không có trang tính.";
                    return View();
                }

                var headerRow = worksheet.FirstRowUsed();

                if (headerRow == null)
                {
                    ViewBag.Error = "File Excel không có dữ liệu.";
                    return View();
                }

                // Đọc tên cột ở dòng đầu tiên
                var headers = headerRow.CellsUsed()
                    .ToDictionary(
                        c => c.GetString().Trim(),
                        c => c.Address.ColumnNumber,
                        StringComparer.OrdinalIgnoreCase);

                string[] requiredHeaders =
                {
            "TitleBook",
            "Publisher",
            "Author",
            "PublicationYear",
            "CategoryID",
            "BookQuantity"
        };

                var missingHeaders = requiredHeaders
                    .Where(h => !headers.ContainsKey(h))
                    .ToList();

                if (missingHeaders.Any())
                {
                    ViewBag.Error =
                        "Thiếu các cột bắt buộc: " +
                        string.Join(", ", missingHeaders);

                    return View();
                }

                // Lấy các thể loại hợp lệ
                var categories = await _context.Categories
                    .Where(c => c.IsActive)
                    .Select(c => c.CategoryID)
                    .ToListAsync();

                int GetInt(
                    IXLWorksheet sheet,
                    int row,
                    string column)
                {
                    return int.TryParse(
                        sheet.Cell(row, headers[column]).GetFormattedString()
                            .Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int value)
                        ? value
                        : int.MinValue;
                }

                string GetText(
                    IXLWorksheet sheet,
                    int row,
                    string column)
                {
                    return headers.ContainsKey(column)
                        ? sheet.Cell(row, headers[column])
                            .GetString().Trim()
                        : string.Empty;
                }

                var firstDataRow = headerRow.RowNumber() + 1;
                var lastDataRow = worksheet.LastRowUsed()?.RowNumber()
                                  ?? firstDataRow - 1;

                for (int row = firstDataRow; row <= lastDataRow; row++)
                {
                    // Bỏ qua dòng trống
                    if (worksheet.Row(row).IsEmpty())
                        continue;

                    var title = GetText(worksheet, row, "TitleBook");
                    var publisher = GetText(worksheet, row, "Publisher");
                    var author = GetText(worksheet, row, "Author");

                    int year = GetInt(worksheet, row, "PublicationYear");
                    int categoryId = GetInt(worksheet, row, "CategoryID");
                    int quantity = GetInt(worksheet, row, "BookQuantity");

                    var rowErrors = new List<string>();

                    if (string.IsNullOrWhiteSpace(title))
                        rowErrors.Add("Thiếu tiêu đề sách");

                    if (string.IsNullOrWhiteSpace(publisher))
                        rowErrors.Add("Thiếu nhà xuất bản");

                    if (string.IsNullOrWhiteSpace(author))
                        rowErrors.Add("Thiếu tác giả");

                    if (year < 1 || year > DateTime.Now.Year)
                        rowErrors.Add("Năm xuất bản không hợp lệ");

                    if (categoryId == int.MinValue ||
                        !categories.Contains(categoryId))
                    {
                        rowErrors.Add("Mã thể loại không tồn tại hoặc đã ngừng hoạt động");
                    }

                    if (quantity < 0 || quantity == int.MinValue)
                        rowErrors.Add("Số lượng sách không hợp lệ");

                    if (rowErrors.Any())
                    {
                        errors.Add(
                            $"Dòng {row}: {string.Join("; ", rowErrors)}");

                        continue;
                    }

                    var book = new tblBook
                    {
                        TitleBook = title,
                        Publisher = publisher,
                        Author = author,
                        PublicationYear = year,
                        CategoryID = categoryId,
                        BookQuantity = quantity,
                        Description = GetText(
                            worksheet, row, "Description"),
                        ImageBook = GetText(
                            worksheet, row, "ImageBook"),
                        IsActive = true,
                        BorrowQuantity = 0,
                        TotalReaders = 0
                    };

                    // Cho phép cột IsActive tùy chọn
                    if (headers.ContainsKey("IsActive"))
                    {
                        var activeText = GetText(
                            worksheet, row, "IsActive");

                        if (!string.IsNullOrWhiteSpace(activeText))
                        {
                            if (bool.TryParse(activeText, out bool active))
                            {
                                book.IsActive = active;
                            }
                            else if (activeText == "1" ||
                                     activeText.Equals(
                                         "Có",
                                         StringComparison.OrdinalIgnoreCase))
                            {
                                book.IsActive = true;
                            }
                            else if (activeText == "0" ||
                                     activeText.Equals(
                                         "Không",
                                         StringComparison.OrdinalIgnoreCase))
                            {
                                book.IsActive = false;
                            }
                            else
                            {
                                errors.Add(
                                    $"Dòng {row}: IsActive chỉ nhận true, false, 1, 0, Có hoặc Không.");
                                continue;
                            }
                        }
                    }

                    importedBooks.Add(book);
                }

                // Chỉ lưu khi toàn bộ dữ liệu hợp lệ
                if (errors.Any())
                {
                    ViewBag.Errors = errors;
                    ViewBag.Error =
                        $"Có {errors.Count} lỗi dữ liệu. Chưa có sách nào được lưu.";

                    return View();
                }

                if (importedBooks.Count == 0)
                {
                    ViewBag.Error = "Không tìm thấy dòng sách hợp lệ để nhập.";
                    return View();
                }

                await using var transaction =
                    await _context.Database.BeginTransactionAsync();

                try
                {
                    await _context.Books.AddRangeAsync(importedBooks);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }

                TempData["Success"] =
                    $"Nhập thành công {importedBooks.Count} đầu sách từ Excel.";

                return RedirectToAction(nameof(Index));
            }
            catch (Exception)
            {
                ViewBag.Error =
                    "Không thể đọc file Excel hoặc lưu dữ liệu. " +
                    "Vui lòng kiểm tra định dạng file và cấu trúc dữ liệu.";

                return View();
            }
        }

        [HttpGet]
        public async Task<IActionResult> DownloadExcelTemplate()
        {
            // Lấy danh sách thể loại đang hoạt động
            var categories = await _context.Categories
                .Where(c => c.IsActive)
                .OrderBy(c => c.CategoryID)
                .Select(c => new
                {
                    c.CategoryID,
                    c.CategoryName
                })
                .ToListAsync();

            using var workbook = new XLWorkbook();
            // Sheet 1: Mẫu nhập sách
            var worksheet = workbook.Worksheets.Add("NhapSach");
            string[] headers =
            {
            "TitleBook",
            "Description",
            "Publisher",
            "Author",
            "PublicationYear",
            "CategoryID",
            "BookQuantity",
            "IsActive",
            "ImageBook"
            };
            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cell(1, i + 1).Value = headers[i];
            }
            // Định dạng tiêu đề
            var headerRange = worksheet.Range(1, 1, 1, headers.Length);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Font.FontColor = XLColor.White;
            headerRange.Style.Fill.BackgroundColor = XLColor.DarkBlue;
            headerRange.Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Center;
            headerRange.Style.Alignment.Vertical =
            XLAlignmentVerticalValues.Center;
            headerRange.Style.Alignment.WrapText = true;
            worksheet.Row(1).Height = 30;
            // Hai dòng minh họa
            worksheet.Cell("A2").Value = "Đắc Nhân Tâm";
            worksheet.Cell("B2").Value = "Sách kỹ năng sống";
            worksheet.Cell("C2").Value = "First News";
            worksheet.Cell("D2").Value = "Dale Carnegie";
            worksheet.Cell("E2").Value = 2020;
            worksheet.Cell("F2").Value =
            categories.FirstOrDefault()?.CategoryID ?? 0;
            worksheet.Cell("G2").Value = 20;
            worksheet.Cell("H2").Value = true;
            worksheet.Cell("I2").Value = "";
            worksheet.Cell("A3").Value = "Nhà Giả Kim";
            worksheet.Cell("B3").Value = "Tiểu thuyết";
            worksheet.Cell("C3").Value = "Nhã Nam";
            worksheet.Cell("D3").Value = "Paulo Coelho";
            worksheet.Cell("E3").Value = 2022;
            worksheet.Cell("F3").Value =
            categories.FirstOrDefault()?.CategoryID ?? 0;
            worksheet.Cell("G3").Value = 15;
            worksheet.Cell("H3").Value = true;
            worksheet.Cell("I3").Value = "";
            // Định dạng dữ liệu
            worksheet.Columns().AdjustToContents();
            worksheet.Column(1).Width = 28;
            worksheet.Column(2).Width = 35;
            worksheet.Column(3).Width = 22;
            worksheet.Column(4).Width = 25;
            worksheet.Column(9).Width = 25;
            worksheet.SheetView.FreezeRows(1);
            worksheet.RangeUsed()!.SetAutoFilter();
            // Sheet 2: Danh mục thể loại
            var categorySheet = workbook.Worksheets.Add("TheLoai");
            categorySheet.Cell("A1").Value = "CategoryID";
            categorySheet.Cell("B1").Value = "CategoryName";
            var categoryHeader = categorySheet.Range("A1:B1");
            categoryHeader.Style.Font.Bold = true;
            categoryHeader.Style.Font.FontColor = XLColor.White;
            categoryHeader.Style.Fill.BackgroundColor = XLColor.DarkBlue;
            for (int i = 0; i < categories.Count; i++)
            {
                categorySheet.Cell(i + 2, 1).Value =
                categories[i].CategoryID;
                categorySheet.Cell(i + 2, 2).Value =
                categories[i].CategoryName;
            }
            categorySheet.Columns().AdjustToContents();
            categorySheet.SheetView.FreezeRows(1);
            // Sheet 3: Hướng dẫn
            var guide = workbook.Worksheets.Add("HuongDan");
            guide.Cell("A1").Value = "HƯỚNG DẪN NHẬP SÁCH TỪ EXCEL";
            guide.Cell("A1").Style.Font.Bold = true;
            guide.Cell("A1").Style.Font.FontSize = 14;
            guide.Cell("A3").Value =
            "1. Nhập dữ liệu sách tại sheet NhapSach.";
            guide.Cell("A4").Value =
            "2. Giữ nguyên tên các cột ở dòng đầu tiên.";
            guide.Cell("A5").Value =
            "3. CategoryID phải tồn tại và đang hoạt động trong hệ thống.";
            guide.Cell("A6").Value =
            "4. BookQuantity là số nguyên không âm.";
            guide.Cell("A7").Value =
            "5. PublicationYear phải là năm hợp lệ, không lớn hơn năm hiện tại.";
            guide.Cell("A8").Value =
            "6. IsActive nhận true, false, 1, 0, Có hoặc Không.";
            guide.Cell("A9").Value =
            "7. ImageBook là đường dẫn ảnh đã có trên máy chủ; có thể để trống.";
            guide.Cell("A10").Value =
            "8. Xóa các dòng minh họa trước khi nhập dữ liệu thật.";
            guide.Cell("A11").Value =
            "9. BookID, BorrowQuantity và TotalReaders do hệ thống quản lý.";
            guide.Column(1).Width = 100;
            guide.Range("A1:A11").Style.Alignment.WrapText = true;
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            var fileBytes = stream.ToArray();
            return File(
            fileBytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "Mau_Nhap_Sach_LibraryHub.xlsx"
            );
        }
    }
}