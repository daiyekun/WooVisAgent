var builder = DistributedApplication.CreateBuilder(args);

var httpapi = builder.AddProject<Projects.WooVisAgent_Api>("woovisagent-api");

builder.AddViteApp("woovisagent-webui", "../WooVisAgent.Web")
    .WithEndpoint("http", endpointAnnotation =>
    {
        endpointAnnotation.Port = 5199;
    })
    .WaitFor(httpapi)
    .WithReference(httpapi);

builder.Build().Run();
