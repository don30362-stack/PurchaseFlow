using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace PurchaseFlow.Api.Tests;

public class AuthEndpointTests
{
    public class TestApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
            {
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] =
                            "Server=invalid-host;" +
                            "Database=PurchaseFlowTests;" +
                            "Integrated Security=True;" +
                            "Connect Timeout=1;"
                    });
            });

            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Me_WithoutLogin_Returns401()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            }
        );

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Csrf_ReturnsToken()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            }
        );

        var response = await client.GetAsync("api/auth/csrf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(json.RootElement.TryGetProperty("csrfToken", out var token));

        Assert.False(string.IsNullOrWhiteSpace(token.GetString()));
    }

    [Fact]
    public async Task Login_WithoutCsrfToken_Returns400()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            }
        );

        var response = await client.PostAsJsonAsync(
            "api/auth/login",
            new
            {
                userName = "employee",
                password = "unused"
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}