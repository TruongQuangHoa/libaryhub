using LibraryHub.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using LibraryHub.Services;
using Microsoft.Extensions.FileProviders;
using LibraryHub.Services.Chat;

var builder = WebApplication.CreateBuilder(args);

// Đăng ký các Service
builder.Services.AddScoped<ReturnService>();
builder.Services.AddScoped<IFeePaymentService, FeePaymentService>();
builder.Services.AddScoped<IPaymentReconciler, PaymentReconciler>();
builder.Services.AddScoped<LibraryStatisticsService>();

// THÊM SESSION (bắt buộc cho giỏ mượn)
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(10);      // Thời gian sống session
    options.Cookie.HttpOnly = true;                      // Bảo mật: không cho JS truy cập
    options.Cookie.IsEssential = true;                   // Bắt buộc phải có (GDPR)
    options.Cookie.Name = "LibraryHub.BorrowingCart";    // Tên cookie session (tùy chọn)
});

// Cấu hình DbContext
var connection = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<DataContext>(options =>
    options.UseSqlServer(connection));

builder.Services.AddAuthentication(
    CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login/Index";
        options.LogoutPath = "/Login/Logout";
        options.AccessDeniedPath = "/Home/AccessDenied";

        options.Cookie.Name = "LibraryHub.AuthCookie";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        // Cho phép gia hạn cookie khi người dùng vẫn hoạt động.
        options.SlidingExpiration = true;

        // Thời gian hết hạn nếu không được gia hạn.
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    });

// Thêm MVC
builder.Services.AddControllersWithViews();

// Cấu hình ChatbotOptions từ appsettings.json
builder.Services.Configure<ChatbotOptions>(
    builder.Configuration.GetSection("Gemini"));

builder.Services.Configure<ChatbotServiceOptions>(
    builder.Configuration.GetSection("ChatbotService"));

builder.Services.Configure<VietQrPaymentOptions>(
    builder.Configuration.GetSection("VietQrPayment"));

builder.Services.AddHttpClient<IGeminiChatService, GeminiChatService>(
    client =>
    {
        client.BaseAddress = new Uri(
            "https://generativelanguage.googleapis.com/v1beta/");

        client.Timeout = TimeSpan.FromSeconds(60);
    });

builder.Services.AddHttpClient<IPythonChatbotClient, PythonChatbotClient>((sp, client) =>
{
    var options = sp.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<ChatbotServiceOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.BaseUrl))
    {
        var baseUrl = options.BaseUrl.TrimEnd('/') + "/";
        client.BaseAddress = new Uri(baseUrl);
    }

    var timeoutSeconds = options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 60;
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseStaticFiles(new StaticFileOptions()
{
    FileProvider = new PhysicalFileProvider(
                  Path.Combine(Directory.GetCurrentDirectory(), "uploads")
    ),
    RequestPath = "/files"
});

app.UseRouting();


app.UseSession();

app.UseAuthentication();

// Kiểm tra thời gian không hoạt động của Admin/Librarian.
app.Use(async (context, next) =>
{
    var user = context.User;

    bool isManagementUser =
        user.Identity?.IsAuthenticated == true &&
        (user.IsInRole("Admin") || user.IsInRole("Librarian"));

    if (!isManagementUser)
    {
        await next();
        return;
    }

    const string lastActivityKey = "ManagementLastActivityUtc";

    var lastActivityText = context.Session.GetString(lastActivityKey);

    if (DateTimeOffset.TryParse(lastActivityText, out var lastActivity))
    {
        if (DateTimeOffset.UtcNow - lastActivity >=
            TimeSpan.FromMinutes(10))
        {
            await context.SignOutAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);

            context.Session.Clear();

            context.Response.Redirect("/Login/Logout?idle=1");
            return;
        }
    }
    else
    {
        // Chưa có mốc thời gian: khởi tạo.
        context.Session.SetString(
            lastActivityKey,
            DateTimeOffset.UtcNow.ToString("O"));
    }

    await next();
});

app.UseAuthorization();



// Nếu bạn có extension method MapStaticAssets() và WithStaticAssets()
app.MapStaticAssets();

// Route cho Area (Admin, ...)
app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

// Route mặc định
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets(); // Nếu extension này tồn tại

app.Run();
