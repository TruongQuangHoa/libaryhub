using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LibraryHub.Models;
using Microsoft.EntityFrameworkCore;

namespace LibraryHub.Services
{
    public interface IFeePaymentService
    {
        Task<tblPaymentSetting> GetActiveSettingAsync();
        Task<decimal> GetOverdueFinePerDayAsync();
        Task<int> CalculateOverdueDaysAsync(DateTime? dueDate, DateTime? returnDate);
        Task<decimal> CalculateOverdueFineAsync(DateTime? dueDate, DateTime? returnDate);
        Task<string> BuildVietQrImageUrlAsync(decimal amount, string? addInfo = null);
        string BuildVietQrPayload(string bankBin, string accountNumber, decimal amount, string? addInfo, string? accountName);
    }

    public class FeePaymentService : IFeePaymentService
    {
        private readonly DataContext _context;

        public FeePaymentService(DataContext context)
        {
            _context = context;
        }

        public async Task<tblPaymentSetting> GetActiveSettingAsync()
        {
            try
            {
                var setting = await _context.PaymentSettings
                    .AsNoTracking()
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.SettingID)
                    .FirstOrDefaultAsync();

                if (setting != null)
                {
                    return setting;
                }
            }
            catch
            {
                // Bảng tblPaymentSetting chưa được tạo trong CSDL -> dùng cấu hình mặc định
            }

            return new tblPaymentSetting
            {
                SettingID = 0,
                OverdueFinePerDay = 5000m,
                BankBin = "970415",
                BankAccountNumber = "102877362936",
                BankAccountName = "VIETINBANK",
                BankName = "Vietinbank",
                IsActive = true
            };
        }

        public async Task<decimal> GetOverdueFinePerDayAsync()
        {
            var setting = await GetActiveSettingAsync();
            return setting.OverdueFinePerDay < 0 ? 0m : setting.OverdueFinePerDay;
        }

        public Task<int> CalculateOverdueDaysAsync(DateTime? dueDate, DateTime? returnDate)
        {
            if (!dueDate.HasValue || !returnDate.HasValue)
            {
                return Task.FromResult(0);
            }

            var days = (returnDate.Value.Date - dueDate.Value.Date).Days;
            return Task.FromResult(days > 0 ? days : 0);
        }

        public async Task<decimal> CalculateOverdueFineAsync(DateTime? dueDate, DateTime? returnDate)
        {
            var days = await CalculateOverdueDaysAsync(dueDate, returnDate);
            if (days <= 0)
            {
                return 0m;
            }

            var rate = await GetOverdueFinePerDayAsync();
            return days * rate;
        }

        public async Task<string> BuildVietQrImageUrlAsync(decimal amount, string? addInfo = null)
        {
            var setting = await GetActiveSettingAsync();
            var amountValue = Math.Max(0, decimal.ToInt64(decimal.Truncate(amount)));
            var info = SanitizeAddInfo(addInfo);
            var accountName = Uri.EscapeDataString(setting.BankAccountName ?? string.Empty);
            var encodedInfo = Uri.EscapeDataString(info);
            var bin = Uri.EscapeDataString(setting.BankBin ?? "970415");
            var account = Uri.EscapeDataString(setting.BankAccountNumber ?? "12345");

            var queryParts = new System.Collections.Generic.List<string>();
            if (amountValue > 0)
            {
                queryParts.Add($"amount={amountValue}");
            }
            if (!string.IsNullOrWhiteSpace(info))
            {
                queryParts.Add($"addInfo={encodedInfo}");
            }
            if (!string.IsNullOrWhiteSpace(setting.BankAccountName))
            {
                queryParts.Add($"accountName={accountName}");
            }

            var baseUrl = $"https://img.vietqr.io/image/{bin}-{account}-compact2.png";
            return queryParts.Count > 0 ? $"{baseUrl}?{string.Join("&", queryParts)}" : baseUrl;
        }

        public string BuildVietQrPayload(string bankBin, string accountNumber, decimal amount, string? addInfo, string? accountName)
        {
            var amountValue = Math.Max(0, decimal.ToInt64(decimal.Truncate(amount)));
            var info = SanitizeAddInfo(addInfo);
            var name = NormalizeAscii(accountName ?? string.Empty).ToUpperInvariant();
            if (name.Length > 25) name = name.Substring(0, 25);

            var guid = "A000000727";
            var service = "QRIBFTTA"; // to account
            var beneficiary = Tlv("00", bankBin) + Tlv("01", accountNumber);
            var consumerAccountInfo = Tlv("00", guid) + Tlv("01", beneficiary) + Tlv("02", service);
            var additionalData = string.IsNullOrWhiteSpace(info) ? string.Empty : Tlv("08", info);

            var payload = new StringBuilder();
            payload.Append(Tlv("00", "01"));
            payload.Append(Tlv("01", "12"));
            payload.Append(Tlv("38", consumerAccountInfo));
            payload.Append(Tlv("53", "704"));
            if (amountValue > 0)
            {
                payload.Append(Tlv("54", amountValue.ToString(CultureInfo.InvariantCulture)));
            }
            payload.Append(Tlv("58", "VN"));
            if (!string.IsNullOrWhiteSpace(name))
            {
                payload.Append(Tlv("59", name));
            }
            payload.Append(Tlv("60", "VIETNAM"));
            if (!string.IsNullOrWhiteSpace(additionalData))
            {
                payload.Append(Tlv("62", additionalData));
            }

            var crcInput = payload.ToString() + "6304";
            var crc = ComputeCrc16Ccitt(Encoding.ASCII.GetBytes(crcInput));
            payload.Append("6304");
            payload.Append(crc.ToString("X4"));
            return payload.ToString();
        }

        private static string Tlv(string id, string value)
        {
            var len = Encoding.UTF8.GetByteCount(value ?? string.Empty).ToString("D2", CultureInfo.InvariantCulture);
            return id + len + (value ?? string.Empty);
        }

        private static string SanitizeAddInfo(string? addInfo)
        {
            var text = NormalizeAscii(addInfo ?? string.Empty);
            var sb = new StringBuilder();
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '_' || ch == '.')
                {
                    sb.Append(ch);
                }
                else if (char.IsWhiteSpace(ch))
                {
                    sb.Append(' ');
                }
            }
            var result = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (result.Length > 25) result = result.Substring(0, 25);
            return result;
        }

        private static string NormalizeAscii(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var normalized = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != UnicodeCategory.NonSpacingMark)
                {
                    var c = ch == 'đ' ? 'd' : ch == 'Đ' ? 'D' : ch;
                    if (c <= 127) sb.Append(c);
                    else sb.Append(' ');
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static ushort ComputeCrc16Ccitt(byte[] bytes)
        {
            ushort crc = 0xFFFF;
            const ushort poly = 0x1021;
            foreach (var b in bytes)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0)
                        crc = (ushort)((crc << 1) ^ poly);
                    else
                        crc <<= 1;
                }
            }
            return crc;
        }
    }
}
