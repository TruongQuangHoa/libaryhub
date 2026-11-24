using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LibraryHub.Models
{
    [Table("tblUnmatchedBankTransaction")]
    public class tblUnmatchedBankTransaction
    {
        [Key]
        public int UnmatchedID { get; set; }

        [Required]
        [StringLength(50)]
        public string Provider { get; set; } = "SePay";

        [StringLength(100)]
        public string? ProviderTransactionId { get; set; }

        [StringLength(100)]
        public string? ReferenceCode { get; set; }

        [StringLength(100)]
        public string? Gateway { get; set; }

        [StringLength(50)]
        public string? AccountNumber { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal TransferAmount { get; set; }

        [StringLength(500)]
        public string? Content { get; set; }

        [StringLength(250)]
        public string? Reason { get; set; }

        public string? RawPayload { get; set; }

        public DateTime TransactionDate { get; set; } = DateTime.Now;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public bool IsResolved { get; set; } = false;
    }
}
