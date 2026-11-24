using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// LibraryHub.Models/BorrowingCartItem.cs
namespace LibraryHub.Models
{
    public class BorrowingCartItem
    {
        public int BookID { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public decimal BorrowingFee { get; set; }     // Phí mượn của quyển này
        public int Quantity { get; set; } = 1;
    }
}