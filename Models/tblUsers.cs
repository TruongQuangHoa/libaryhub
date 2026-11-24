using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace LibraryHub.Models
{
    [Table("tblUsers")]
    public class tblUsers
    {
        [Key]
        public int UserID { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Password { get; set; }
        public bool IsActive { get; set; } = true;
        public virtual ICollection<tblUsersRoles> UserRoles { get; set; } = new List<tblUsersRoles>();
    }
}