using CdcPay.PayQueryApi;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddPayQueryApi();

WebApplication app = builder.Build();
app.MapPayQueryApi();
app.Run();
