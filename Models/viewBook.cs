using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace LibraryHub.Models
{
    [Table("viewBook")]
    public class viewBook
    {
        [Key]
        public int BookID { get; set; }
        public string TitleBook { get; set; }
        public string? Description { get; set; }
        public string? Publisher { get; set; }
        public string? Author { get; set; }
        public string? ImageBook { get; set; }
        public bool IsActive { get; set; } = true;
        public int BookQuantity { get; set; }
        public int BorrowQuantity { get; set; }
        public int PublicationYear { get; set; }
        public int TotalReaders { get; set; }
        public int CategoryID { get; set; }
        public string? CategoryName { get; set; }
    }
}