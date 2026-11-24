using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;

namespace LibraryHub.Components
{
    [ViewComponent(Name = "Category")]
    public class CategoryComponent : ViewComponent
    {
        private readonly DataContext _context;

        public CategoryComponent(DataContext context)
        {
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var listOfCategory = (from p in _context.Categories
                              where (p.IsActive == true)
                              orderby p.CategoryID ascending
                              select p).Take(12).ToList();
            return await Task.FromResult((IViewComponentResult)View("Default", listOfCategory));
        }
    }
}