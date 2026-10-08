using ImmichFrame.Core.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ImmichFrame.WebApi.Helpers;

/// <summary>
/// Answers 404 for an <see cref="AssetNotFoundException"/> escaping a frame action, which nothing
/// mapped: it was a 500.
/// <para>
/// The usual way to get one is an asset id this process never handed out. Which account holds an
/// asset is looked up among the assets the accounts have served (<c>BloomFilterAssetAccountTracker</c>),
/// and that lookup starts empty - after a restart, or once a configuration save has rebuilt the
/// profile - while the frames still hold the ids of the slideshow they were shown before it. Asking
/// after one of those is asking for something that is not here, which is a 404.
/// </para>
/// <para>
/// A filter rather than middleware like <see cref="Profiles.ProfileNotFoundMiddleware"/>: this one is
/// thrown from inside an action, where MVC's filters see it, and that one is thrown where they do not.
/// And one exception type, for the reason that middleware gives - every other domain exception means
/// something went wrong, and mapping them here would hide it.
/// </para>
/// <para>
/// Be clear that a 404 is also what an account failing to serve an asset it does hold now reads as.
/// The lookup moves on to the next account whatever the failure was, taking it for a false positive
/// of the Bloom filter, and reports the asset as not found once no account is left; the cause is in
/// the warning it logs for each account, not in this status.
/// </para>
/// </summary>
public sealed class AssetNotFoundFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        // A response already on its way can no longer be given a status of its own.
        if (context.Exception is not AssetNotFoundException || context.HttpContext.Response.HasStarted)
        {
            return;
        }

        // The same problem details ControllerBase.Problem would produce. Not the exception's message:
        // that is written for the log, and the tracker's says nothing at all.
        var problem = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>()
            .CreateProblemDetails(context.HttpContext, StatusCodes.Status404NotFound,
                detail: "That asset was not found on any of this frame's accounts.");

        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status404NotFound };
        context.ExceptionHandled = true;
    }
}
