using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LibraryHub.Models.ViewModels
{
    public class ProfileViewModel
    {
        public tblPatron Patron { get; set; }
        public List<tblBorrowing> BorrowingHistory { get; set; }
        // Thêm các trường cho đổi mật khẩu
        public string CurrentPassword { get; set; }
        public string NewPassword { get; set; }
        public string ConfirmPassword { get; set; }
    }
}