using ImmichFrame.Core.Api;
using ImmichFrame.Core.Exceptions;
using ImmichFrame.WebApi.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace ImmichFrame.WebApi.Tests.Helpers;

/// <summary>
/// Which exceptions <see cref="AssetNotFoundFilterAttribute"/> answers for, each handed to it directly
/// on a bare context. The 404 it makes of an asset no account holds, through a real host, is
/// <c>AssetControllerTests.GetById_AssetNoAccountIsFoundFor_IsNotFound</c>.
/// </summary>
[TestFixture]
public class AssetNotFoundFilterAttributeTests
{
    private ServiceProvider _services;

    [SetUp]
    public void Setup()
    {
        // What the filter builds its problem details with, registered the way MVC registers it.
        _services = new ServiceCollection().AddMvcCore().Services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _services.Dispose();
    }

    /// <summary>
    /// The control the test below needs: on the very same context the filter does answer, so leaving
    /// every other exception alone is the filter's choice, not a context it could do nothing with.
    /// </summary>
    [Test]
    public void OnException_AssetNotFound_IsHandledAsNotFound()
    {
        // Arrange
        var context = Failed(new AssetNotFoundException());

        // Act
        new AssetNotFoundFilterAttribute().OnException(context);

        // Assert
        var result = context.Result as ObjectResult;
        Assert.Multiple(() =>
        {
            Assert.That(context.ExceptionHandled, Is.True);
            Assert.That(result?.StatusCode, Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That((result?.Value as ProblemDetails)?.Status, Is.EqualTo(StatusCodes.Status404NotFound));
        });
    }

    /// <summary>
    /// Any other exception means something went wrong rather than that something is not there, and a
    /// 404 would hide it. It goes on out unhandled, to be answered as the failure it is. The filter sits
    /// on the whole controller, so it sees both of these kinds: the generated client's, for a status
    /// Immich answered with that nothing here maps, and a plain bug.
    /// </summary>
    [TestCaseSource(nameof(OtherFailures))]
    public void OnException_AnyOtherException_IsLeftUnhandled(Exception exception)
    {
        // Arrange
        var context = Failed(exception);

        // Act
        new AssetNotFoundFilterAttribute().OnException(context);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(context.ExceptionHandled, Is.False);
            Assert.That(context.Result, Is.Null);
        });
    }

    private static IEnumerable<TestCaseData> OtherFailures()
    {
        yield return new TestCaseData(new ApiException("Unexpected status code (500).", StatusCodes.Status500InternalServerError,
                string.Empty, new Dictionary<string, IEnumerable<string>>(), null!))
            .SetArgDisplayNames(nameof(ApiException));
        yield return new TestCaseData(new InvalidOperationException("Sequence contains no elements"))
            .SetArgDisplayNames(nameof(InvalidOperationException));
    }

    /// <summary>What MVC hands an exception filter once an action has thrown <paramref name="exception"/>.</summary>
    private ExceptionContext Failed(Exception exception)
    {
        var httpContext = new DefaultHttpContext { RequestServices = _services };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        return new ExceptionContext(actionContext, []) { Exception = exception };
    }
}
