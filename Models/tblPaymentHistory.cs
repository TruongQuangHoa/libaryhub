using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LibraryHub.Models
{
    [Table("tblPaymentHistory")]
    public class tblPaymentHistory
    {
        [Key]
        public int PaymentID { get; set; }

        public int ReturnID { get; set; }

        [ForeignKey(nameof(ReturnID))]
        public tblReturn? _return { get; set; }

        public int BorrowingID { get; set; }

        public int? PatronID { get; set; }

        [StringLength(200)]
        public string? PatronName { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [Required]
        [StringLength(50)]
        public string PaymentMethod { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        public string PaymentStatus { get; set; } = "Đã thanh toán";

        [StringLength(100)]
        public string? TransactionCode { get; set; }

        [StringLength(50)]
        public string? Provider { get; set; }

        [StringLength(100)]
        public string? ProviderTransactionId { get; set; }

        public string? RawPayload { get; set; }

        [StringLength(250)]
        public string? TransactionContent { get; set; }

        [StringLength(100)]
        public string? BankName { get; set; }

        [StringLength(50)]
        public string? BankAccountNumber { get; set; }

        [StringLength(200)]
        public string? ConfirmedBy { get; set; }

        public DateTime PaymentDate { get; set; } = DateTime.Now;
    }
}
