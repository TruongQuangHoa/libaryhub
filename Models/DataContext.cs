using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LibraryHub.Areas.Admin.Models;

namespace LibraryHub.Models
{
    public class DataContext : DbContext
    {
        public DataContext(DbContextOptions<DataContext> options) : base(options)
        {
        }
        public DbSet<tblMenu> Menus { get; set; }
        public DbSet<AdminMenu> AdminMenus { get; set; }
        public DbSet<tblCategory> Categories { get; set; }
        public DbSet<tblLibrarian> Librarians { get; set; }
        public DbSet<tblBook> Books { get; set; }
        public DbSet<tblPatron> Patrons { get; set; }
        public DbSet<tblBorrowing> Borrowings { get; set; }
        public DbSet<tblBorrowingDetail> BorrowingDetails { get; set; }
        public DbSet<viewBook> viewBooks { get; set; }
        public DbSet<tblUsers> tblUsers { get; set; }
        public DbSet<tblRoles> tblRoles { get; set; }
        public DbSet<tblUsersRoles> tblUsersRoles { get; set; }
        public DbSet<tblReturn> Returns { get; set; }
        public DbSet<tblReturnDetail> ReturnDetails { get; set; }
        public DbSet<tblAuthor> Authors { get; set; }

        public DbSet<tblLibraryReview> LibraryReviews { get; set; }

        public DbSet<tblPost> Posts { get; set; }

        public DbSet<tblPublisher> Publishers { get; set; }
        public DbSet<tblPaymentSetting> PaymentSettings { get; set; }
        public DbSet<tblPaymentHistory> PaymentHistories { get; set; }
        public DbSet<tblUnmatchedBankTransaction> UnmatchedBankTransactions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // 1. Cấu hình khóa chính composite cho bảng trung gian tblUsersRoles
            modelBuilder.Entity<tblUsersRoles>()
                .HasKey(ur => new { ur.UserID, ur.RoleID });


            // Cấu hình khóa phụ duy nhất cho PatronID trong tblParont
            modelBuilder.Entity<tblPatron>()
                .HasAlternateKey(hs => hs.PatronID);

            // Quan hệ Sách -> Thể loại (N - 1)
            modelBuilder.Entity<tblBook>()
                .HasOne(x => x.category)
                .WithMany(x => x.book)
                .HasForeignKey(x => x.CategoryID)
                .HasPrincipalKey(x => x.CategoryID);

            // Quan hệ Phiếu mượn -> Độc giả (N - 1)
            modelBuilder.Entity<tblBorrowing>()
                .HasOne(x => x.patron)
                .WithMany(x => x.borrowing)
                .HasForeignKey(x => x.PatronID)
                .HasPrincipalKey(x => x.PatronID);

            // Quan hệ Phiếu mượn -> Nhân viên (N - 1)
            modelBuilder.Entity<tblBorrowing>()
                .HasOne(x => x.librarian)
                .WithMany(x => x.borrowing)
                .HasForeignKey(x => x.LibrarianID)
                .HasPrincipalKey(x => x.LibrarianID);

            // Quan hệ Chi tiết phiếu mượn -> Phiếu mượn (N - 1)
            modelBuilder.Entity<tblBorrowingDetail>()
                .HasOne(x => x.borrowing)
                .WithMany(x => x.borrowingDetail)
                .HasForeignKey(x => x.BorrowingID)
                .HasPrincipalKey(x => x.BorrowingID);

            // Quan hệ Chi tiết phiếu mượn -> Sách (N - 1)
            modelBuilder.Entity<tblBorrowingDetail>()
                .HasOne(x => x.book)
                .WithMany(x => x.borrowingDetail)
                .HasForeignKey(x => x.BookID)
                .HasPrincipalKey(x => x.BookID);

            // Quan hệ Phiếu trả -> Phiếu mượn (1 - 1)
            modelBuilder.Entity<tblReturn>()
                .HasOne(x => x.borrowing)
                .WithMany()
                .HasForeignKey(x => x.BorrowingID)
                .HasPrincipalKey(x => x.BorrowingID);

            // Quan hệ Phiếu trả -> Nhân viên (N - 1)
            modelBuilder.Entity<tblReturn>()
                .HasOne(x => x.librarian)
                .WithMany(x => x._return)
                .HasForeignKey(x => x.LibrarianID)
                .HasPrincipalKey(x => x.LibrarianID);

            // Quan hệ Chi tiết phiếu trả -> Phiếu trả (N - 1)
            modelBuilder.Entity<tblReturnDetail>()
                .HasOne(x => x._return)
                .WithMany(x => x.returnDetail)
                .HasForeignKey(x => x.ReturnID)
                .HasPrincipalKey(x => x.ReturnID);

            // Quan hệ Chi tiết phiếu trả -> Sách (N - 1)
            modelBuilder.Entity<tblReturnDetail>()
                .HasOne(x => x.book)
                .WithMany(x => x.returnDetail)
                .HasForeignKey(x => x.BookID)
                .HasPrincipalKey(x => x.BookID);

            // Slug bài viết phải duy nhất
            modelBuilder.Entity<tblPost>()
                .HasIndex(p => p.Slug)
                .IsUnique();

            // Quan hệ Nhà xuất bản -> Tác giả (1 - N)
            modelBuilder.Entity<tblAuthor>()
                .HasOne(x => x.Publisher)
                .WithMany(x => x.Authors)
                .HasForeignKey(x => x.PublisherID)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<tblPaymentSetting>().HasData(new tblPaymentSetting
            {
                SettingID = 1,
                OverdueFinePerDay = 5000m,
                BankBin = "970415",
                BankAccountNumber = "12345",
                BankAccountName = "VIETINBANK",
                BankName = "Vietinbank",
                IsActive = true
            });

            // Gọi 1 lần duy nhất
            base.OnModelCreating(modelBuilder);
        }
    }
}