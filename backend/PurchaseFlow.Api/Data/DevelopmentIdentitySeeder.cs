using Microsoft.AspNetCore.Identity;

namespace PurchaseFlow.Api.Data;

public static class DevelopmentIdentitySeeder
{
    public static async Task SeedRolesAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();

        var roleManager = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole>>();

        string[] roles = ["Employee", "Manager"];

        foreach (var roleName in roles)
        {
            if (await roleManager.RoleExistsAsync(roleName))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(
                new IdentityRole(roleName));

            if (!result.Succeeded)
            {
                var errors = string.Join(
                    "; ",
                    result.Errors.Select(error => error.Description));

                throw new InvalidOperationException(
                    $"Failed to create role '{roleName}': {errors}");
            }
        }
    }

    public static async Task SeedUsersAsync(
    IServiceProvider services,
    IConfiguration configuration)
    {
        var employeePassword = configuration["DevSeed:EmployeePassword"];
        var managerPassword = configuration["DevSeed:ManagerPassword"];

        // 未設定開發帳號密碼時，跳過帳號初始化。
        if (employeePassword is null && managerPassword is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(employeePassword) ||
            string.IsNullOrWhiteSpace(managerPassword))
        {
            throw new InvalidOperationException(
                "Development user passwords are incomplete.");
        }

        using var scope = services.CreateScope();

        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();

        var accounts = new[]
        {
        (UserName: "employee",
         Email: "employee@purchaseflow.test",
         Role: "Employee",
         Password: employeePassword),

        (UserName: "manager",
         Email: "manager@purchaseflow.test",
         Role: "Manager",
         Password: managerPassword)
        };

        foreach (var account in accounts)
        {
            var existingUser =
                await userManager.FindByNameAsync(account.UserName);

            if (existingUser is not null)
            {
                var existingRoles =
                    await userManager.GetRolesAsync(existingUser);

                if (existingRoles.Count != 1 ||
                    existingRoles[0] != account.Role)
                {
                    throw new InvalidOperationException(
                        $"Existing user '{account.UserName}' has an unexpected role.");
                }

                continue;
            }

            var user = new ApplicationUser
            {
                UserName = account.UserName,
                Email = account.Email,
                EmailConfirmed = true
            };

            var createResult =
                await userManager.CreateAsync(user, account.Password);

            if (!createResult.Succeeded)
            {
                var errors = string.Join("; ",
                    createResult.Errors.Select(e => e.Description));

                throw new InvalidOperationException(
                    $"Failed to create '{account.UserName}': {errors}");
            }

            var roleResult =
                await userManager.AddToRoleAsync(user, account.Role);

            if (!roleResult.Succeeded)
            {
                // 避免保留新建但沒有角色的測試帳號。
                var deleteResult = await userManager.DeleteAsync(user);

                var errors = string.Join("; ",
                    roleResult.Errors.Select(e => e.Description));

                throw new InvalidOperationException(
                    $"Failed to assign role to '{account.UserName}': {errors}. " +
                    $"Cleanup succeeded: {deleteResult.Succeeded}");
            }
        }
    }
}