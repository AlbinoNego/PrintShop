using Microsoft.AspNetCore.Mvc;

namespace PrintShop.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();

    // Destino do UseExceptionHandler em producao: nunca mostra detalhes do erro ao cliente.
    [IgnoreAntiforgeryToken]
    public IActionResult Error()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View("Error", 500);
    }

    // Destino do UseStatusCodePagesWithReExecute para 404, 403 e afins.
    [IgnoreAntiforgeryToken]
    // O codigo chega como {id} pela rota padrao (/Home/Status/404). FromRoute porque a
    // reexecucao repete o formulario original, que pode ter um campo "id" proprio.
    public IActionResult Status([FromRoute] int id)
    {
        Response.StatusCode = id is >= 400 and <= 599 ? id : StatusCodes.Status404NotFound;
        return View("Error", Response.StatusCode);
    }
}
