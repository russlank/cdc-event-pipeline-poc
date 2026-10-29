using CdcPay.Common.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(typeof(Program).Assembly);

WebApplication app = builder.Build();
app.MapServiceEndpoints();
app.Run();
