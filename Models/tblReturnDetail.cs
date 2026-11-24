using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace LibraryHub.Models
{
    [Table("tblReturnDetail")]
    public class tblReturnDetail
    {
        [Key]
        public int ReturnDetailID { get; set; }
        public int ReturnID { get; set; }
        [ForeignKey(nameof(ReturnID))]
        public tblReturn? _return { get; set; }
        public int BookID { get; set; }
        public bool IsDamaged { get; set; }
        [Column(TypeName = "decimal(18,2)")] // Tốt hơn nên để 18,2 để có phần thập phân
        public decimal LostFine { get; set; }
        public int OverdueDays { get; set; }
        [Column(TypeName = "decimal(18,2)")] // Tốt hơn nên để 18,2 để có phần thập phân
        public decimal OverdueFine { get; set; }
        public bool IsActive { get; set; }
        // Quan hệ ngược lại sách
        [JsonIgnore] // chặn vòng lặp
        [ForeignKey(nameof(BookID))]
        public virtual tblBook? book { get; set; }
    }
}