using EquipmentManager.Components;
using EquipmentManager.Services;
using EquipmentManager.Services.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRenderProxy(builder.Configuration);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// Строка подключения не читается из appsettings или аргументов командной строки.
var profileCredentials = new ConfigurationBuilder();
if (builder.Environment.IsDevelopment())
{
    profileCredentials.AddUserSecrets(typeof(Program).Assembly, optional: true);
}
profileCredentials.AddEnvironmentVariables();
var protectedCredentials = profileCredentials.Build();
builder.Services.AddEquipmentStorage(builder.Configuration, protectedCredentials);
builder.Services.AddEquipmentAuthentication(builder.Configuration, protectedCredentials);
builder.Services.AddEquipmentUiAuthentication(builder.Configuration, protectedCredentials);

var app = builder.Build();
// Схема запроса восстанавливается до HSTS, HTTPS redirect, cookie и antiforgery.
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapEquipmentApi();
app.MapEquipmentUiAccount();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization(UiAuthentication.Policy);

app.Run();
