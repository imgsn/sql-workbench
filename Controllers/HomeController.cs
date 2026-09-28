using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Workbench.Models;
namespace Workbench.Controllers;
public class HomeController : Controller
{
    public IActionResult Index(string tool = "schema", bool demo = false)
    {
        var titles = new Dictionary<string, string> {
            ["schema"] = "Schema compare", ["data"] = "Data compare",
            ["explorer"] = "Database explorer", ["connections"] = "Connections",
            ["scripts"] = "Schema scripts", ["inserts"] = "INSERT generator", ["query"] = "Query editor"
        };
        if (!titles.TryGetValue(tool, out var title)) return NotFound();
        return View(new WorkbenchViewModel(tool, title, demo));
    }
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
