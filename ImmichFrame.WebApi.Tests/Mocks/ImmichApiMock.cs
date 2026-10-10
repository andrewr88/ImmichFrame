using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmichFrame.Core.Api;
using ImmichFrame.WebApi.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Moq;
using Moq.Protected;

namespace ImmichFrame.WebApi.Tests.Mocks
{
    /// <summary>
    /// Shared test helpers for mocking the Immich API behind ImmichFrame's <c>ImmichApiAccountClient</c>.
    /// </summary>
    public static class ImmichApiMock
    {
        /// <summary>
        /// Sets up the handler to answer <c>GET server/version</c> - by default with a version this
        /// ImmichFrame supports. Every host sends that request at startup, to each server it is
        /// configured with: the check is fire-and-forget and only logs what it hears, so nothing fails
        /// without this, but the request would go out to the network instead.
        /// </summary>
        public static Mock<HttpMessageHandler> WithServerVersion(
            this Mock<HttpMessageHandler> handler,
            long major = ImmichServerVersionChecker.MinimumSupportedMajorVersion,
            long minor = ImmichServerVersionChecker.MinimumSupportedMinorVersion,
            long patch = ImmichServerVersionChecker.MinimumSupportedPatchVersion,
            long? prerelease = null)
        {
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("server/version")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(
                        $"{{\"major\":{major},\"minor\":{minor},\"patch\":{patch},\"prerelease\":{prerelease?.ToString() ?? "null"}}}")
                });

            return handler;
        }

        /// <summary>
        /// Sets up the handler to answer the statistics search an unnarrowed account counts its
        /// assets with for <c>GetTotalAssets</c>, so a profile's graph can be asked for a real number.
        /// That call is made behind the account's API cache, which is the piece a torn-down graph has
        /// disposed.
        /// </summary>
        public static Mock<HttpMessageHandler> WithAssetStatistics(this Mock<HttpMessageHandler> handler, long images)
        {
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("search/statistics")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(JsonSerializer.Serialize(new SearchStatisticsResponseDto
                    {
                        Total = images
                    }))
                });

            return handler;
        }

        /// <summary>
        /// Sets up the handler to answer the random search an unnarrowed account makes for
        /// <c>GetAssets</c> with no assets at all, so a frame whose account is a real one can be asked
        /// for its slideshow without a photo library behind it.
        /// </summary>
        public static Mock<HttpMessageHandler> WithNoRandomAssets(this Mock<HttpMessageHandler> handler)
        {
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("search/random")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(() => new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("[]")
                });

            return handler;
        }

        /// <summary>
        /// Registers the mock handler as the primary <see cref="HttpMessageHandler"/> for every named
        /// <see cref="System.Net.Http.HttpClient"/> created by <see cref="IHttpClientFactory"/>.
        /// </summary>
        public static IServiceCollection UseMockHandler(this IServiceCollection services, Mock<HttpMessageHandler> handler)
        {
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
            {
                options.HttpMessageHandlerBuilderActions.Add(b =>
                {
                    b.PrimaryHandler = handler.Object;
                });
            });

            return services;
        }
    }
}
