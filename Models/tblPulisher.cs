
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LibraryHub.Models
{
    [Table("tblPublisher")]
    public class tblPublisher
    {
        [Key]
        public int PublisherID { get; set; }

        [Required(ErrorMessage = "Vui lòng nhập tên nhà xuất bản.")]
        [StringLength(255)]
        public string PublisherName { get; set; }

        [Column(TypeName = "nvarchar(max)")]
        public string? Description { get; set; }

        [StringLength(500)]
        public string? Address { get; set; }

        [StringLength(20)]
        public string? Phone { get; set; }

        [StringLength(255)]
        public string? Email { get; set; }

        [StringLength(500)]
        public string? Website { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedDate { get; set; } = DateTime.Now;

        public DateTime ModifiedDate { get; set; } = DateTime.Now;

        // Quan hệ 1 - N với tblAuthor
        public ICollection<tblAuthor> Authors { get; set; }
            = new List<tblAuthor>();
    }
}
