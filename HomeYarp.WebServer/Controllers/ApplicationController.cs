using Microsoft.AspNetCore.Mvc;

namespace HomeYarp.WebApi.Controllers
{
    public class ApplicationController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
