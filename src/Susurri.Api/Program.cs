using Blazored.LocalStorage;
using Microsoft.AspNetCore.ResponseCompression;
using MudBlazor.Services;
using Susurri.Api;
using Susurri.Api.Commands;
using Susurri.Api.Commands.Handlers;
using Susurri.Api.Components;
using Susurri.Api.Controllers;
using Susurri.Api.Repositories;
using Susurri.Application;
using Susurri.Application.Abstractions;
using Susurri.Core;
using Susurri.Core.Abstractions;
using Susurri.Core.Hubs;
using Susurri.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
// Add services to the container.
builder.Services.AddRazorPages();
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();


builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/octet-stream" });
});

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<UserController>();

builder.Services.AddTransient<SignInHandler>();
builder.Services.AddTransient<SignUpHandler>();
builder.Services.AddScoped(typeof(ICommandHandler<SignUp>), typeof(SignUpHandler));
builder.Services.AddScoped(typeof(ICommandHandler<SignIn>), typeof(SignInHandler));

builder.Services.AddCors();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddCore()
    .AddInfrastructure(builder.Configuration)
    .AddApplication();

var app = builder.Build();

app.UseInfrastructure();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment()) app.UseResponseCompression();

app.UseCors(policy => policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());

app.UseSession();
app.UseAuthentication();
app.UseRouting();
app.UseAuthorization();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseResponseCompression();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHub<ChatHub>("/chathub");
app.UseUsersApi();
await app.RunAsync();