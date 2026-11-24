
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Components
{
    public class PostViewComponent : ViewComponent
    {
        private readonly DataContext _context;

        public PostViewComponent(DataContext context)
        {
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var posts = await _context.Posts
                .AsNoTracking()
                .Where(p =>
                    p.IsActive &&
                    p.IsPublished &&
                    p.PublishedDate != null &&
                    p.PublishedDate <= DateTime.Now)
                .OrderByDescending(p => p.IsFeatured)
                .ThenByDescending(p => p.PublishedDate)
                .Take(6)
                .ToListAsync();

            return View(posts);
        }
    }
}
