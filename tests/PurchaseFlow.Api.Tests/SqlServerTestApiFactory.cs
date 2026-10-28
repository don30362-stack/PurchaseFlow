using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace PurchaseFlow.Api.Tests;

public sealed class SqlServerTestApiFactory
    : WebApplicationFactory<Program>
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
                        @"Server=.\SQLEXPRESS;" +
                        "Database=PurchaseFlowTestsDb;" +
                        "Trusted_Connection=True;" +
                        "Encrypt=True;" +
                        "TrustServerCertificate=True;"
                });
        });

        return base.CreateHost(builder);
    }
}