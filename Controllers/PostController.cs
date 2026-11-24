
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Controllers
{
    public class PostController : Controller
    {
        private readonly DataContext _context;

        public PostController(DataContext context)
        {
            _context = context;
        }

        [HttpGet]
        [Route("bai-viet-{slug}-{id:int}.html")]
        public async Task<IActionResult> Details(string slug, int id)
        {
            var post = await _context.Posts
                .FirstOrDefaultAsync(p =>
                    p.PostID == id &&
                    p.IsActive &&
                    p.IsPublished &&
                    p.PublishedDate != null &&
                    p.PublishedDate <= DateTime.Now);

            if (post == null)
            {
                return NotFound();
            }

            // Chuyển về URL chuẩn nếu slug trên đường dẫn không đúng.
            if (!string.Equals(
                slug,
                post.Slug,
                StringComparison.OrdinalIgnoreCase))
            {
                return Redirect($"/bai-viet-{post.Slug}-{post.PostID}.html");
            }

            // Tăng lượt xem.
            post.ViewCount++;
            await _context.SaveChangesAsync();

            // Lấy các bài viết liên quan cùng danh mục.
            ViewBag.RelatedPosts = await _context.Posts
                .AsNoTracking()
                .Where(p =>
                    p.PostID != post.PostID &&
                    p.IsActive &&
                    p.IsPublished &&
                    p.PublishedDate != null &&
                    p.PublishedDate <= DateTime.Now &&
                    p.Category == post.Category)
                .OrderByDescending(p => p.PublishedDate)
                .Take(3)
                .ToListAsync();

            ViewBag.MetaTitle = !string.IsNullOrWhiteSpace(post.MetaTitle)
                ? post.MetaTitle
                : post.Title;

            ViewBag.MetaDescription = post.MetaDescription
                ?? post.Summary;

            return View(post);
        }
    }
}
