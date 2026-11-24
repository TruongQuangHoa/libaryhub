using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Http;
using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;

namespace LibraryHub.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin, Librarian")]
    public class CategoryController : Controller
    {
        private readonly DataContext _context;

        public CategoryController(DataContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            var hslist = _context.Categories.OrderBy(h => h.CategoryID).ToList();

            return View(hslist);
        }

        public IActionResult Delete(int? id)
        {
            if (id == null || id == 0)
                return NotFound();
            var hs = _context.Categories.Find(id);
            if (id == null)
                return NotFound();
            return View(hs);
        }
        [HttpPost]
        public IActionResult Delete(int id)
        {
            var delhs = _context.Categories.Find(id);
            if (delhs == null)
                return NotFound();
            _context.Categories.Remove(delhs);
            _context.SaveChanges();
            return RedirectToAction("Index");
        }
        public IActionResult Create()
        {
            var mnList = (from m in _context.Categories
                          select new SelectListItem()
                          {
                              Text = (m.CategoryID == 1) ? m.CategoryName : "-- " + m.CategoryName,
                              Value = m.CategoryID.ToString()
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
        public IActionResult Create(tblCategory mn)
        {
            if (ModelState.IsValid)
            {
                _context.Categories.Add(mn);
                _context.SaveChanges();
                return RedirectToAction("Index");
            }

            return View(mn);
        }

        public IActionResult Edit(int? id)
        {
            if (id == null || id == 0)
                return NotFound();

            var dp = _context.Categories.Find(id);
            if (dp == null)
                return NotFound();

            var dpList = (from c in _context.Categories
                          select new SelectListItem()
                          {
                              Text = (c.CategoryID == 1) ? c.CategoryName : "-- " + c.CategoryName,
                              Value = c.CategoryID.ToString()
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
        public IActionResult Edit(tblCategory dp)
        {
            if (ModelState.IsValid)
            {
                _context.Update(dp);
                _context.SaveChanges();
                return RedirectToAction("Index");
            }

            return View(dp);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportExcel(IFormFile? excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                TempData["ImportError"] = "Vui lòng chọn file Excel cần nhập.";
                return RedirectToAction(nameof(Index));
            }

            if (!Path.GetExtension(excelFile.FileName)
                .Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                TempData["ImportError"] = "Chỉ hỗ trợ file Excel định dạng .xlsx.";
                return RedirectToAction(nameof(Index));
            }

            if (excelFile.Length > 5 * 1024 * 1024)
            {
                TempData["ImportError"] = "File Excel không được vượt quá 5 MB.";
                return RedirectToAction(nameof(Index));
            }

            int imported = 0;
            int skipped = 0;
            var errors = new List<string>();

            try
            {
                using var stream = new MemoryStream();
                await excelFile.CopyToAsync(stream);
                stream.Position = 0;

                using var document = SpreadsheetDocument.Open(stream, false);

                var workbookPart = document.WorkbookPart;
                var sheet = workbookPart?.Workbook.Sheets?
                    .Elements<Sheet>()
                    .FirstOrDefault();

                if (sheet == null || workbookPart == null)
                {
                    TempData["ImportError"] = "File Excel không có worksheet.";
                    return RedirectToAction(nameof(Index));
                }

                var worksheetPart = (WorksheetPart)workbookPart
                    .GetPartById(sheet.Id!);

                var sheetData = worksheetPart.Worksheet
                    .GetFirstChild<SheetData>();

                if (sheetData == null)
                {
                    TempData["ImportError"] = "Không tìm thấy dữ liệu trong Excel.";
                    return RedirectToAction(nameof(Index));
                }

                var sharedStrings = workbookPart.SharedStringTablePart?
                    .SharedStringTable;

                // Lấy các tên thể loại đã tồn tại trong database.
                var existingNames = await _context.Categories
                    .Select(c => c.CategoryName)
                    .ToListAsync();

                var knownNames = new HashSet<string>(
                    existingNames.Where(n => !string.IsNullOrWhiteSpace(n))
                                 .Select(n => n.Trim()),
                    StringComparer.OrdinalIgnoreCase);

                var categoriesToAdd = new List<tblCategory>();

                // Bỏ qua dòng đầu tiên vì đó là tiêu đề cột.
                foreach (var row in sheetData.Elements<Row>().Skip(1))
                {
                    string name = ReadCell(row, "A", sharedStrings).Trim();
                    string description = ReadCell(row, "B", sharedStrings).Trim();
                    string icon = ReadCell(row, "C", sharedStrings).Trim();
                    string activeText = ReadCell(row, "D", sharedStrings).Trim();

                    // Bỏ qua dòng trống.
                    if (string.IsNullOrWhiteSpace(name) &&
                        string.IsNullOrWhiteSpace(description) &&
                        string.IsNullOrWhiteSpace(icon) &&
                        string.IsNullOrWhiteSpace(activeText))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        skipped++;
                        errors.Add($"Dòng {row.RowIndex}: Thiếu tên thể loại.");
                        continue;
                    }

                    if (name.Length > 50)
                    {
                        skipped++;
                        errors.Add($"Dòng {row.RowIndex}: Tên thể loại vượt quá 50 ký tự.");
                        continue;
                    }

                    if (!knownNames.Add(name))
                    {
                        skipped++;
                        errors.Add($"Dòng {row.RowIndex}: Thể loại \"{name}\" đã tồn tại hoặc bị trùng trong file.");
                        continue;
                    }

                    bool isActive = true;

                    if (!string.IsNullOrWhiteSpace(activeText))
                    {
                        if (activeText.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                            activeText.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                            activeText.Equals("Có", StringComparison.OrdinalIgnoreCase))
                        {
                            isActive = true;
                        }
                        else if (
                            activeText.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
                            activeText.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                            activeText.Equals("Không", StringComparison.OrdinalIgnoreCase))
                        {
                            isActive = false;
                        }
                        else
                        {
                            skipped++;
                            errors.Add($"Dòng {row.RowIndex}: IsActive phải là TRUE hoặc FALSE.");
                            continue;
                        }
                    }

                    categoriesToAdd.Add(new tblCategory
                    {
                        CategoryName = name,
                        Description = string.IsNullOrWhiteSpace(description)
                            ? null : description,
                        Icon = string.IsNullOrWhiteSpace(icon)
                            ? null : icon,
                        IsActive = isActive
                    });
                }

                if (categoriesToAdd.Count > 0)
                {
                    await _context.Categories.AddRangeAsync(categoriesToAdd);
                    await _context.SaveChangesAsync();
                    imported = categoriesToAdd.Count;
                }

                TempData["ImportSuccess"] =
                    $"Nhập thành công {imported} thể loại; bỏ qua {skipped} dòng.";

                if (errors.Count > 0)
                {
                    TempData["ImportDetails"] = string.Join("\n", errors.Take(20));

                    if (errors.Count > 20)
                    {
                        TempData["ImportDetails"] +=
                            $"\n... và {errors.Count - 20} lỗi khác.";
                    }
                }
            }
            catch (Exception ex)
            {
                // Ghi log lỗi thực tế trong môi trường triển khai.
                TempData["ImportError"] =
                    "Không thể đọc file Excel. Vui lòng kiểm tra định dạng và dữ liệu của file.";
            }

            return RedirectToAction(nameof(Index));
        }

        private static string ReadCell(
            Row row,
            string column,
            SharedStringTable? sharedStrings)
        {
            var cell = row.Elements<Cell>()
                .FirstOrDefault(c =>
                    c.CellReference?.Value?.StartsWith(
                        column, StringComparison.OrdinalIgnoreCase) == true);

            if (cell == null)
                return string.Empty;

            string value = cell.CellValue?.InnerText ?? string.Empty;

            if (cell.DataType?.Value == CellValues.SharedString &&
                int.TryParse(value, out int index) &&
                sharedStrings != null)
            {
                return sharedStrings
                    .Elements<SharedStringItem>()
                    .ElementAtOrDefault(index)?
                    .InnerText ?? string.Empty;
            }

            if (cell.DataType?.Value == CellValues.InlineString)
                return cell.InlineString?.InnerText ?? string.Empty;

            return value;
        }


        [HttpGet]
        public IActionResult DownloadExcelTemplate()
        {
            using var stream = new MemoryStream();

            using (var document = SpreadsheetDocument.Create(
                stream,
                SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();

                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();

                // Hàm tạo ô dạng văn bản.
                Cell CreateTextCell(string column, uint rowIndex, string value)
                {
                    return new Cell
                    {
                        CellReference = $"{column}{rowIndex}",
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(
                            new Text(value ?? string.Empty))
                    };
                }

                // Dòng tiêu đề.
                var header = new Row { RowIndex = 1 };
                header.Append(
                    CreateTextCell("A", 1, "CategoryName"),
                    CreateTextCell("B", 1, "Description"),
                    CreateTextCell("C", 1, "Icon"),
                    CreateTextCell("D", 1, "IsActive")
                );
                sheetData.Append(header);

                // Dòng dữ liệu ví dụ.
                var sample1 = new Row { RowIndex = 2 };
                sample1.Append(
                    CreateTextCell("A", 2, "Công nghệ thông tin"),
                    CreateTextCell("B", 2, "Sách lập trình và công nghệ"),
                    CreateTextCell("C", 2, "bi bi-cpu"),
                    CreateTextCell("D", 2, "TRUE")
                );
                sheetData.Append(sample1);

                var sample2 = new Row { RowIndex = 3 };
                sample2.Append(
                    CreateTextCell("A", 3, "Văn học"),
                    CreateTextCell("B", 3, "Sách văn học Việt Nam và thế giới"),
                    CreateTextCell("C", 3, "bi bi-book"),
                    CreateTextCell("D", 3, "TRUE")
                );
                sheetData.Append(sample2);

                worksheetPart.Worksheet = new Worksheet(sheetData);
                worksheetPart.Worksheet.Save();

                var sheets = workbookPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = 1,
                    Name = "TheLoaiSach"
                });

                workbookPart.Workbook.Save();
            }

            return File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "MauNhapTheLoaiSach.xlsx"
            );
        }


    }
}