using API.Data;
using System.Text.Json.Serialization;
using API.Entities;
using API.Middleware;
using API.RequestHelpers;
using API.Services;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.Configure<InvoicingSettings>(builder.Configuration.GetSection("InvoicingSettings"));
builder.Services.Configure<API.Services.Chatbot.ChatbotSettings>(builder.Configuration.GetSection("Chatbot"));
builder.Services.Configure<API.Services.Chatbot.AnthropicSettings>(builder.Configuration.GetSection("Anthropic"));
builder.Services.AddScoped<IEmailService, SendGridEmailService>();
builder.Services.AddScoped<INewsletterSender, SendGridNewsletterSender>();
builder.Services.AddHostedService<NewsletterDispatcher>();
builder.Services.Configure<CloudinarySettings>(builder.Configuration.GetSection("Cloudinary"));
builder.Services.AddControllers().AddJsonOptions(opt =>
{
    // serialize enums as their string names so frontend receives readable genero values
    opt.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // prevent reference-cycle serialization errors when entities have back-references
    opt.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});
builder.Services.AddDbContext<StoreContext>(opt => 
{
    opt.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));

    // EF Core warning 20606 (Model.Validation): optional owned type with all-nullable properties.
    // This is expected for Product.Dimensoes (it should be null when all fields are null).
    opt.ConfigureWarnings(w => w.Ignore(new Microsoft.Extensions.Logging.EventId(
        20606,
        "OptionalDependentWithDependentEntityWithoutIdentifyingPropertyWarning")));
});
builder.Services.AddCors();
builder.Services.AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies());
builder.Services.AddTransient<ExceptionMiddleware>();
builder.Services.AddScoped<PaymentsService>();
builder.Services.AddScoped<ImageService>();
builder.Services.AddScoped<DiscountService>();
builder.Services.AddScoped<IInvoicePdfService, InvoicePdfService>();
builder.Services.AddScoped<API.Services.Invoicing.ITaxInvoiceService, API.Services.Invoicing.TaxInvoiceService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<AccountDeletionService>();
builder.Services.AddScoped<StripeReversalService>();

builder.Services.AddRateLimiter(options =>
{
    // Basic abuse protection for public endpoints (chatbot).
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("chat", opt =>
    {
        opt.PermitLimit = 30;
        opt.Window = TimeSpan.FromMinutes(10);
        opt.QueueLimit = 0;
    });
});

builder.Services.AddHttpClient("anthropic", client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com");
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddScoped<API.Services.Chatbot.IChatbotService, API.Services.Chatbot.AnthropicChatbotService>();
builder.Services.AddIdentityApiEndpoints<User>(opt =>
{
    opt.User.RequireUniqueEmail = true;
})
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<StoreContext>();

// Ensure correct scheme/remote IP behind reverse proxies (Azure App Service).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Log a clear signal if the DB connection string is missing.
var defaultConnection = app.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(defaultConnection))
{
    app.Logger.LogCritical("[DB] Connection string 'DefaultConnection' is missing. Configure it in App Service (Connection strings) or via env var ConnectionStrings__DefaultConnection / SQLCONNSTR_DefaultConnection.");
}

// Controls whether the app should crash on DB init failure.
// Default: false (keep the site up even if DB is temporarily unreachable).
// Configure via App Service setting: DbInit__FailFast=true
var dbInitFailFast = app.Configuration.GetValue("DbInit:FailFast", false);

// Configure the HTTP request pipeline.
app.UseMiddleware<ExceptionMiddleware>();

app.UseForwardedHeaders();

app.UseDefaultFiles();
app.UseStaticFiles();

// Add COOP header to allow Google popup communication
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("Cross-Origin-Opener-Policy", "same-origin-allow-popups");
    await next();
});

app.UseCors(opt => 
{
    var origins = new[] 
    { 
        "https://localhost:3000",
        "https://restore-course-alumn.azurewebsites.net"
    };
    
    opt.AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .WithOrigins(origins);
});

    app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGroup("api").MapIdentityApi<User>(); // api/login
app.MapFallbackToController("Index", "Fallback");

// Apply migrations + seed on startup (with retry) so production has required tables and roles.
if (!string.IsNullOrWhiteSpace(defaultConnection))
{
    for (var attempt = 1; attempt <= 5; attempt++)
    {
        try
        {
            await DbInitializer.InitDb(app);
            break;
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "[DB] Init failed (attempt {Attempt}/5)", attempt);
            if (attempt == 5)
            {
                if (dbInitFailFast)
                {
                    throw;
                }

                app.Logger.LogError("[DB] Init failed after max retries. Continuing startup (DbInit:FailFast=false).");
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
        }
    }
}
else
{
    app.Logger.LogError("[DB] Skipping DB init because connection string is missing.");
}

app.Run();