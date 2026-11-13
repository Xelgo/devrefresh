using Microsoft.AspNetCore.Identity;

namespace DevRefresh;

public static class TestContourBootstrap
{
    public static void Initialize(WebApplication app)
    {
        if(!app.Configuration.GetValue("TestContour:Enabled",false))return;
        var store=app.Services.GetRequiredService<Store>();
        if(store.GetAdmin() is null && app.Configuration["TestContour:AdminPassword"] is {Length:>0} password)
        {
            Rules.ValidatePassword(password);var name=app.Configuration["TestContour:AdminUsername"]??"testadmin";
            Rules.ValidateAdmin(name,password);var admin=new LocalAdmin(name,"",Guid.NewGuid().ToString("N"));
            var hasher=app.Services.GetRequiredService<IPasswordHasher<LocalAdmin>>();
            store.Initialize(admin with{PasswordHash=hasher.HashPassword(admin,password)},[]);
        }
    }
}
