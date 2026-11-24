
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.ViewComponents
{
    public class AuthorViewComponent : ViewComponent
    {
        private readonly DataContext _context;

        public AuthorViewComponent(DataContext context)
        {
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync(int count = 4)
        {
            var authors = await _context.Authors
                .AsNoTracking()
                .Where(a => a.IsActive)
                .OrderByDescending(a => a.TotalReaders)
                .ThenByDescending(a => a.BookCount)
                .ThenBy(a => a.FullName)
                .Take(count)
                .ToListAsync();

            return View(authors);
        }
    }
}
