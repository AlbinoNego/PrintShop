using Microsoft.AspNetCore.Mvc;

namespace PrintShop.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
}
