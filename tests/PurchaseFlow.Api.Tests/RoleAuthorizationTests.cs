using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PurchaseFlow.Api.Authorization;

namespace PurchaseFlow.Api.Tests;

public class RoleAuthorizationTests
{
    [Theory]
    [InlineData("Employee", AuthorizationPolicies.EmployeeOnly, true)]
    [InlineData("Employee", AuthorizationPolicies.ManagerOnly, false)]
    [InlineData("Manager", AuthorizationPolicies.EmployeeOnly, false)]
    [InlineData("Manager", AuthorizationPolicies.ManagerOnly, true)]
    public async Task RolePolicy_EnforcesExpectedRole(
        string role,
        string policyName,
        bool expected)
    {
        using var factory = new AuthEndpointTests.TestApiFactory();
        using var scope = factory.Services.CreateScope();

        var authorization = scope.ServiceProvider
            .GetRequiredService<IAuthorizationService>();

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Role, role)
            },
            authenticationType: "Test");

        var user = new ClaimsPrincipal(identity);

        var result = await authorization.AuthorizeAsync(
            user,
            resource: null,
            policyName: policyName);

        Assert.Equal(expected, result.Succeeded);
    }

    [Theory]
    [InlineData(AuthorizationPolicies.EmployeeOnly)]
    [InlineData(AuthorizationPolicies.ManagerOnly)]
    public async Task AnonymousUser_CannotAccessRolePolicy(string policyName)
    {
        using var factory = new AuthEndpointTests.TestApiFactory();
        using var scope = factory.Services.CreateScope();

        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await authorization.AuthorizeAsync(
            anonymous,
            resource: null,
            policyName: policyName
        );

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackPolicy_RequiresAuthentication(bool authenticated)
    {
        using var factory = new AuthEndpointTests.TestApiFactory();
        using var scope = factory.Services.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();

        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();


        var fallbackPolicy = await provider.GetFallbackPolicyAsync();

        Assert.NotNull(fallbackPolicy);

        var identity = authenticated ? new ClaimsIdentity(authenticationType: "Test") : new ClaimsIdentity();

        var user = new ClaimsPrincipal(identity);

        var result = await authorization.AuthorizeAsync(
            user,
            resource: null,
            policy: fallbackPolicy
        );

        Assert.Equal(authenticated, result.Succeeded);
    }
}