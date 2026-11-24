using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.AspNetCore.Mvc;

namespace LibraryHub.Components
{
    [ViewComponent(Name = "Book")]
    public class BookComponent : ViewComponent
    {
        private readonly DataContext _context;

        public BookComponent(DataContext context)
        {
            _context = context;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var listOfBook = (from p in _context.viewBooks
                              where (p.IsActive == true)
                              orderby p.BookID ascending
                              select p).Take(10).ToList();
            return await Task.FromResult((IViewComponentResult)View("Default", listOfBook));
        }
    }
}