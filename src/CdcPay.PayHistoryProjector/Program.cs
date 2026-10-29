using CdcPay.Common.Hosting;
using CdcPay.PayHistoryProjector;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddPayHistoryProjector();

WebApplication app = builder.Build();
app.MapServiceEndpoints();
app.Run();
