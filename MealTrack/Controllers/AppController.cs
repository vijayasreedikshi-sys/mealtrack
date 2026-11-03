using MealTrack.Infrastructure;
using MealTrack.Models;
using Microsoft.AspNetCore.Mvc;

namespace MealTrack.Controllers;

/// <summary>Base class with the logged-in user and flash-message helpers.</summary>
public abstract class AppController : Controller
{
    /// <summary>The logged-in user (only use inside [Authorize] actions).</summary>
    protected AppUser Me => HttpContext.CurrentUser()
        ?? throw new InvalidOperationException("No logged-in user for this request.");

    protected void Flash(string category, string message) => TempData.AddFlash(category, message);

    protected static string HomeUrlFor(string role) => role switch
    {
        Roles.Customer => "/customer",
        Roles.Admin => "/admin",
        Roles.Seller => "/seller",
        _ => "/login",
    };
}

public class HomeController : AppController
{
    [HttpGet("/")]
    public IActionResult Index()
    {
        var user = HttpContext.CurrentUser();
        return Redirect(user is null ? "/login" : HomeUrlFor(user.Role));
    }
}

public class ErrorController : Controller
{
    [Route("/error/{code:int}")]
    public IActionResult Show(int code)
    {
        var vm = code switch
        {
            403 => new ErrorVm { Code = 403, Title = "Access denied", Message = "You do not have permission to open this page." },
            404 => new ErrorVm { Code = 404, Title = "Page not found", Message = "The page you are looking for does not exist." },
            400 => new ErrorVm { Code = 400, Title = "Bad request", Message = "The request could not be understood." },
            _ => new ErrorVm { Code = 500, Title = "Something went wrong", Message = "An unexpected error occurred. Please try again." },
        };
        Response.StatusCode = vm.Code;
        return View("Error", vm);
    }
}
