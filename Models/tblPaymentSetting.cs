using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LibraryHub.Models
{
    [Table("tblPaymentSetting")]
    public class tblPaymentSetting
    {
        [Key]
        public int SettingID { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal OverdueFinePerDay { get; set; } = 5000m;

        [StringLength(20)]
        public string BankBin { get; set; } = "970415";

        [StringLength(50)]
        public string BankAccountNumber { get; set; } = "12345";

        [StringLength(200)]
        public string? BankAccountName { get; set; } = "VIETINBANK";

        [StringLength(100)]
        public string? BankName { get; set; } = "Vietinbank";

        public bool IsActive { get; set; } = true;

        public DateTime? UpdatedAt { get; set; }
    }
}
