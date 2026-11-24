using Microsoft.AspNetCore.Mvc;

namespace LibraryHub.Controllers
{
    public class IntroductionController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}