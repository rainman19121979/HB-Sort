using System.Net;
using System.Net.Http;
using System.Text;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den BrickognizeClient mit einem custom HttpMessageHandler-Mock.
/// Wir bauen keinen externen Mocking-Framework ein – das geht mit eigenem
/// FakeHandler einfacher und ohne zusaetzliche Dependencies.
/// </summary>
public class BrickognizeClientTests
{
    [Fact]
    public async Task Health_check_returns_online_for_200()
    {
        var handler = FakeHandler.Sync((req, ct) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"ok\"}")
        });

        var client = NewClient(handler);
        var health = await client.CheckHealthAsync();

        Assert.Equal(BrickognizeStatus.Online, health.Status);
        Assert.NotNull(health.ResponseTimeMs);
    }

    [Fact]
    public async Task Health_check_returns_offline_for_500()
    {
        var handler = FakeHandler.Sync((req, ct) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("oops")
            });

        var client = NewClient(handler);
        var health = await client.CheckHealthAsync();

        Assert.Equal(BrickognizeStatus.Offline, health.Status);
    }

    [Fact]
    public async Task Health_check_returns_offline_when_request_throws()
    {
        var handler = FakeHandler.Sync((req, ct) =>
        {
            throw new HttpRequestException("connection refused");
        });

        var client = NewClient(handler);
        var health = await client.CheckHealthAsync();

        Assert.Equal(BrickognizeStatus.Offline, health.Status);
        Assert.Contains("connection refused", health.ErrorMessage);
    }

    [Fact]
    public async Task Predict_minifig_calls_figs_endpoint_and_parses_response()
    {
        string? capturedPath = null;
        string? capturedContentType = null;
        bool capturedFieldName = false;

        var handler = FakeHandler.Async(async (req, ct) =>
        {
            capturedPath = req.RequestUri?.AbsolutePath;
            // Pruefen, ob das Multipart-Form das query_image-Feld enthaelt.
            // Body als raw Bytes lesen + per Latin-1 dekodieren (preserved Bytes 1:1)
            // weil binaere JPEG-Daten im Body ein UTF-8-Decode brechen koennen.
            var bytes = await req.Content!.ReadAsByteArrayAsync(ct);
            var bodyText = Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
            capturedFieldName = bodyText.Contains("query_image");
            capturedContentType = req.Content!.Headers.ContentType?.MediaType;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleMinifigResponse, Encoding.UTF8, "application/json")
            };
        });

        var client = NewClient(handler);
        var result = await client.PredictMinifigAsync(new byte[] { 1, 2, 3, 4 });

        Assert.Equal("/predict/figs/", capturedPath);
        Assert.Equal("multipart/form-data", capturedContentType);
        Assert.True(capturedFieldName, "Multipart-Feld 'query_image' fehlt im Request");

        // Response wurde korrekt deserialisiert
        Assert.Single(result.Items);
        Assert.Equal("sw0001", result.Items[0].Id);
        Assert.Equal("fig", result.Items[0].Type);
        Assert.Equal(2, result.Items[0].ExternalSites.Count);
    }

    [Fact]
    public async Task Predict_part_calls_parts_endpoint_with_predict_color()
    {
        string? capturedPathAndQuery = null;

        var handler = FakeHandler.Sync((req, ct) =>
        {
            capturedPathAndQuery = req.RequestUri?.PathAndQuery;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}")
            };
        });

        var client = NewClient(handler);
        await client.PredictPartAsync(new byte[] { 0xff });

        Assert.Equal("/predict/parts/?predict_color=true", capturedPathAndQuery);
    }

    [Fact]
    public async Task Predict_generic_calls_predict_endpoint()
    {
        string? capturedPath = null;
        var handler = FakeHandler.Sync((req, ct) =>
        {
            capturedPath = req.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}")
            };
        });

        var client = NewClient(handler);
        await client.PredictGenericAsync(new byte[] { 0xab });

        Assert.Equal("/predict/", capturedPath);
    }

    [Fact]
    public async Task Predict_handles_real_world_response_with_double_bbox()
    {
        // Regression: Bounding-Box-Koordinaten kommen in echten Responses als double,
        // nicht int (validiert 2026-04-30). Test stellt sicher, dass wir das
        // korrekt deserialisieren.
        var handler = FakeHandler.Sync((req, ct) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(RealWorldFigsResponse, Encoding.UTF8, "application/json")
        });

        var client = NewClient(handler);
        var result = await client.PredictMinifigAsync(new byte[] { 1 });

        Assert.NotNull(result.BoundingBox);
        Assert.InRange(result.BoundingBox!.Left, 114.0, 115.0);
        Assert.InRange(result.BoundingBox.Score, 0.9, 1.0);

        // Item korrekt gelesen
        Assert.Single(result.Items);
        Assert.Equal("cty0969", result.Items[0].Id);
        Assert.Equal("fig", result.Items[0].Type);
        // Echte Response hat nur EINE external_site, lowercase name
        Assert.Single(result.Items[0].ExternalSites);
        Assert.Equal("bricklink", result.Items[0].ExternalSites[0].Name);
    }

    [Fact]
    public async Task Predict_throws_on_http_error()
    {
        var handler = FakeHandler.Sync((req, ct) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("bad image")
            });

        var client = NewClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.PredictGenericAsync(new byte[] { 1 }));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static IBrickognizeClient NewClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.brickognize.com"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        return new BrickognizeClient(http);
    }

    /// <summary>
    /// Echte Response vom 2026-04-30 (gekuerzt). Wichtig: bounding_box-Werte
    /// sind double, external_sites hat nur einen Eintrag, name ist lowercase.
    /// </summary>
    private const string RealWorldFigsResponse = """
        {
          "listing_id": "res-3944e119fb0c4c4b",
          "bounding_box": {
            "left": 114.228271484375,
            "upper": 82.00371551513672,
            "right": 491.498046875,
            "lower": 363.5955810546875,
            "image_width": 640.0,
            "image_height": 480.0,
            "score": 0.9203721880912781
          },
          "items": [
            {
              "id": "cty0969",
              "name": "Construction Worker - Female",
              "img_url": "https://storage.googleapis.com/brickognize-static/thumbnails/v2.22/fig/cty0969/0.webp",
              "external_sites": [
                { "name": "bricklink",
                  "url": "https://www.bricklink.com/v2/catalog/catalogitem.page?M=cty0969" }
              ],
              "category": "Town / City / Fire",
              "type": "fig",
              "score": 0.8861980438232422
            }
          ]
        }
        """;

    private const string SampleMinifigResponse = """
        {
          "listing_id": "abc123",
          "bounding_box": {
            "left": 10, "upper": 20, "right": 110, "lower": 120,
            "image_width": 640, "image_height": 480, "score": 0.95
          },
          "items": [
            {
              "id": "sw0001",
              "name": "Stormtrooper",
              "img_url": "https://cdn.brickognize.com/sw0001.jpg",
              "external_sites": [
                { "name": "BrickLink",   "url": "https://www.bricklink.com/v2/catalog/catalogitem.page?M=sw0001" },
                { "name": "Rebrickable", "url": "https://rebrickable.com/minifigs/fig-001549/" }
              ],
              "category": "Star Wars",
              "type": "fig",
              "score": 0.92
            }
          ]
        }
        """;

    /// <summary>
    /// Eigener HttpMessageHandler-Mock, der eine vom Test gesteuerte Funktion
    /// pro Request aufruft. Funktioniert sowohl mit synchronen Lambdas als
    /// auch mit async-Lambdas.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        // Privater Konstruktor – Factory-Methoden disambiguieren Sync/Async-Lambdas
        private FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public static FakeHandler Sync(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> sync)
            => new((req, ct) => Task.FromResult(sync(req, ct)));

        public static FakeHandler Async(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> async)
            => new(async);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
