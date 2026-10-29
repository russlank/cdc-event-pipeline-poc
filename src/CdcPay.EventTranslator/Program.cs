using CdcPay.Common.Hosting;
using CdcPay.EventTranslator;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddEventTranslator();

WebApplication app = builder.Build();
app.MapServiceEndpoints();
app.Run();
